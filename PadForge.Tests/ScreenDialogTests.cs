using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Telemetry;
using PadForge.Controls;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The dialogs of the Dialogs-Large and Dialogs-Screens boards (#574):
    /// Custom Formats, the keyboard screen and Screen Overlay dialog,
    /// Dreamcast Screen, Map Key Lights and the icon picker. Each is made
    /// and read, and never shown.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ScreenDialogTests
    {
        private const BindingFlags Own = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static T Part<T>(FrameworkElement scope, string name) where T : class
        {
            var part = scope.FindName(name) as T;
            Assert.True(part != null, name + " is not a " + typeof(T).Name + " of " + scope.GetType().Name);
            return part;
        }

        /// <summary>Gives a dialog a handle and a layout, so its templates
        /// are made and every binding has run. Nothing reaches a screen.</summary>
        private static void Lay(Window dialog)
        {
            dialog.ShowInTaskbar = false;
            new WindowInteropHelper(dialog).EnsureHandle();
            dialog.ApplyTemplate();
            var root = VisualTreeHelper.GetChildrenCount(dialog) > 0
                ? (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0)
                : (FrameworkElement)dialog.Content;
            root.Measure(new Size(1040, 700));
            root.Arrange(new Rect(0, 0, 1040, 700));
            root.UpdateLayout();
            dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }

        private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject node) continue;
                if (node is T match) yield return match;
                foreach (var deeper in Logical<T>(node)) yield return deeper;
            }
        }

        private static IEnumerable<T> Visual<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var deeper in Visual<T>(child)) yield return deeper;
            }
        }

        /// <summary>The ids of a dialog's resets, in the order its rows run.</summary>
        private static string[] ResetIds(Window dialog)
            => Logical<SettingResetButton>(dialog).Select(AutomationProperties.GetAutomationId).ToArray();

        // ── Custom Formats ──

        private static CustomTelemetryFormatsDialog Formats()
            => new(PageRenderDumpPages.BoardFormats()) { ShowInTaskbar = false };

        /// <summary>Custom Formats is the board's two columns in the Wide
        /// frame at its own size: the formats with Add, Duplicate and Delete
        /// under them, the picked format's rows in the board's order, and a
        /// foot that leads with the problem. The head has no X: Cancel and
        /// Escape close it.</summary>
        [Fact]
        public void TheFormatsDialogIsTheBoardsTwoColumns()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = Formats();
                try
                {
                    var s = Strings.Instance;
                    Assert.True(dialog.IsWide);
                    Assert.False(dialog.ShowClose);
                    Assert.False(dialog.ScrollsBody);
                    Assert.Equal(1040, dialog.Width);
                    Assert.Equal(680, dialog.Height);
                    Assert.Equal(SizeToContent.Manual, dialog.SizeToContent);
                    Assert.Equal(new Thickness(24, 20, 24, 0), dialog.HeadPadding);
                    Assert.Equal(new Thickness(24, 16, 24, 16), dialog.BodyPadding);
                    Assert.Equal(new Thickness(24, 14, 24, 14), dialog.FootPadding);
                    Assert.Equal(s.Telemetry_Formats_Title, dialog.Title);
                    Assert.Equal(s.Telemetry_Formats_Description, dialog.Subtitle);

                    // The formats, as the controller page's lists draw their
                    // items, 4 apart, the first one picked.
                    var list = Part<ListBox>(dialog, "FormatList");
                    Assert.Same(dialog.FindResource("PartList"), list.Style);
                    Assert.Same(dialog.FindResource("PartListItem"), list.ItemContainerStyle.BasedOn);
                    Assert.Contains(list.ItemContainerStyle.Setters.OfType<Setter>(), setter =>
                        setter.Property == FrameworkElement.MarginProperty && (Thickness)setter.Value == new Thickness(0, 0, 0, 4));
                    Assert.Equal(new[] { "Rig Telemetry", "Sim Dashboard" }, list.Items.Cast<TelemetryFormatEdit>().Select(f => f.Name));
                    Assert.Equal(0, list.SelectedIndex);

                    // Add, Duplicate and Delete under the list. Add says Add
                    // and reads Add Format.
                    var add = Part<Button>(dialog, "AddFormatButton");
                    var duplicate = Part<Button>(dialog, "DuplicateFormatButton");
                    var delete = Part<Button>(dialog, "DeleteFormatButton");
                    var column = (Panel)list.Parent;
                    Assert.Same(list, column.Children[0]);
                    var line = Assert.IsType<FlexLinePanel>(column.Children[1]);
                    Assert.Equal(new UIElement[] { add, duplicate, delete }, line.Children.Cast<UIElement>());
                    Assert.Equal(s.Common_Add, add.Content);
                    Assert.Equal(s.Telemetry_Formats_Add, AutomationProperties.GetName(add));
                    Assert.Same(dialog.FindResource("DialogButton"), add.Style);
                    Assert.Same(dialog.FindResource("DialogQuietButton"), duplicate.Style);
                    Assert.Same(dialog.FindResource("DialogQuietDangerButton"), delete.Style);
                    Assert.True(duplicate.IsEnabled);
                    Assert.True(delete.IsEnabled);

                    // The picked format's rows: Name, the packets, Fields with
                    // its paragraph as the heading's tooltip, the heads, the
                    // field rows, Add Field, More.
                    var editor = Part<StackPanel>(dialog, "EditorPanel");
                    Assert.Equal(7, editor.Children.Count);
                    Assert.IsType<SettingRow>(editor.Children[0]);
                    Assert.Same(Part<FlexLinePanel>(dialog, "PacketsLine"), editor.Children[1]);
                    var heading = Assert.IsType<TextBlock>(editor.Children[2]);
                    Assert.Equal(s.Telemetry_Formats_Fields, heading.Text);
                    Assert.Equal(s.Telemetry_Formats_FieldsHint, heading.ToolTip);
                    Assert.Equal(s.Telemetry_Formats_FieldsHint, AutomationProperties.GetHelpText(heading));
                    Assert.Same(dialog.FindResource("DialogHead3"), heading.Style);
                    Assert.Same(Part<Grid>(dialog, "FieldHeads"), editor.Children[3]);
                    Assert.Same(Part<ItemsControl>(dialog, "FieldRows"), editor.Children[4]);
                    var addField = Part<Button>(dialog, "AddFieldButton");
                    Assert.Same(addField, editor.Children[5]);
                    Assert.Equal(s.Telemetry_Formats_AddField, AutomationProperties.GetName(addField));
                    Assert.Equal(new Thickness(-16, 8, 0, 0), addField.Margin);
                    var more = Part<MoreRow>(dialog, "FormatMore");
                    Assert.Same(more, editor.Children[6]);
                    Assert.Same(dialog.FindResource("DialogMoreRow"), more.Style);
                    Assert.Equal(s.Common_More, more.Header);
                    Assert.Equal(s.Telemetry_Formats_More, more.Hint);

                    // The foot: the problem, Cancel, then Save.
                    var foot = Assert.IsType<FlexLinePanel>(dialog.Foot);
                    var problem = Part<TextBlock>(dialog, "ProblemText");
                    var save = Part<Button>(dialog, "SaveButton");
                    Assert.Equal(3, foot.Children.Count);
                    Assert.Same(problem, foot.Children[0]);
                    Assert.True(LiveText.GetPolite(problem));
                    var cancel = Assert.IsType<Button>(foot.Children[1]);
                    Assert.True(cancel.IsCancel);
                    Assert.Equal(s.Common_Cancel, cancel.Content);
                    Assert.Same(save, foot.Children[2]);
                    Assert.True(save.IsDefault);
                    Assert.Same(dialog.FindResource("DialogPrimaryButton"), save.Style);

                    // Two formats with one name: Save says so in the foot.
                    list.Items.Cast<TelemetryFormatEdit>().Last().Name = "Rig Telemetry";
                    typeof(CustomTelemetryFormatsDialog).GetMethod("SaveButton_Click", Own)
                        .Invoke(dialog, new object[] { save, new RoutedEventArgs() });
                    Assert.Equal(string.Format(s.Telemetry_Formats_ErrorDuplicate_Format, "Rig Telemetry"), problem.Text);
                    Assert.Null(dialog.Result);
                }
                finally { dialog.Close(); }

                // With no format the settings are out of sight, and there is
                // nothing to copy or delete.
                var empty = new CustomTelemetryFormatsDialog(null) { ShowInTaskbar = false };
                try
                {
                    Assert.Equal(Visibility.Hidden, Part<UIElement>(empty, "EditorScroll").Visibility);
                    Assert.False(Part<Button>(empty, "DuplicateFormatButton").IsEnabled);
                    Assert.False(Part<Button>(empty, "DeleteFormatButton").IsEnabled);
                    Assert.True(Part<Button>(empty, "AddFormatButton").IsEnabled);
                }
                finally { empty.Close(); }
            });
        }

        /// <summary>Length and Byte Order belong to a binary format and
        /// leave for a JSON one, each with its label. The Byte Order picker
        /// writes the format's byte order, which was a check box.</summary>
        [Fact]
        public void TheFormatRowsFollowTheFormatsKind()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = Formats();
                try
                {
                    Lay(dialog);
                    var format = Assert.IsType<TelemetryFormatEdit>(Part<StackPanel>(dialog, "EditorPanel").DataContext);
                    var groups = Part<FlexLinePanel>(dialog, "PacketsLine").Children.Cast<StackPanel>().ToList();
                    Assert.Equal(3, groups.Count);
                    Assert.All(groups, group => Assert.IsType<TextBlock>(group.Children[0]));
                    var kind = Assert.IsType<ComboBox>(groups[0].Children[1]);
                    var length = Assert.IsType<Wpf.Ui.Controls.NumberBox>(groups[1].Children[1]);
                    var order = Assert.IsType<ComboBox>(groups[2].Children[1]);
                    Assert.Same(TelemetryChoices.Kinds, kind.ItemsSource);
                    Assert.Same(TelemetryChoices.ByteOrders, order.ItemsSource);
                    Assert.Equal(Wpf.Ui.Controls.NumberBoxSpinButtonPlacementMode.Hidden, length.SpinButtonPlacementMode);
                    Assert.False(length.ClearButtonEnabled);
                    Assert.Equal(90, length.Width);
                    Assert.Equal(Strings.Instance.Telemetry_Formats_ByteOrder, ((TextBlock)groups[2].Children[0]).Text);
                    Assert.Equal(Strings.Instance.Telemetry_Formats_ByteOrder_Tooltip, order.ToolTip);
                    Assert.Equal(Strings.Instance.Telemetry_Formats_ByteOrder, AutomationProperties.GetName(order));

                    // No check box of the dialog writes the byte order.
                    Assert.DoesNotContain(Logical<CheckBox>(dialog), box =>
                        System.Windows.Data.BindingOperations.GetBinding(box, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.Path.Path == "BigEndian");

                    Assert.All(groups, group => Assert.Equal(Visibility.Visible, group.Visibility));
                    Assert.Equal(0, order.SelectedIndex);
                    order.SelectedIndex = 1;
                    Assert.True(format.BigEndian);
                    order.SelectedIndex = 0;
                    Assert.False(format.BigEndian);
                    format.BigEndian = true;
                    Assert.Equal(1, order.SelectedIndex);

                    kind.SelectedIndex = 1;
                    Assert.True(format.IsJson);
                    Assert.Equal(Visibility.Visible, groups[0].Visibility);
                    Assert.Equal(Visibility.Collapsed, groups[1].Visibility);
                    Assert.Equal(Visibility.Collapsed, groups[2].Visibility);
                }
                finally { dialog.Close(); }
            });
        }

        /// <summary>More holds the key a JSON packet must carry and a gear
        /// field's own numbers. It opens by itself for a format that uses
        /// either, so a setting in use is never out of sight, and it is
        /// left as the user has it for a format that uses neither.</summary>
        [Fact]
        public void MoreOpensForAFormatThatUsesIt()
        {
            Assert.False(CustomTelemetryFormatsDialog.UsesMore(null));
            Assert.False(CustomTelemetryFormatsDialog.UsesMore(new TelemetryFormatEdit()));
            Assert.True(CustomTelemetryFormatsDialog.UsesMore(new TelemetryFormatEdit { UseGearValues = true }));
            Assert.True(CustomTelemetryFormatsDialog.UsesMore(new TelemetryFormatEdit { Kind = CustomTelemetryKind.Json, Key = "telemetry" }));
            Assert.False(CustomTelemetryFormatsDialog.UsesMore(new TelemetryFormatEdit { Kind = CustomTelemetryKind.Json, Key = "  " }));
            // A binary format reads no key, whatever its box was left holding.
            Assert.False(CustomTelemetryFormatsDialog.UsesMore(new TelemetryFormatEdit { Key = "left over" }));

            DialogTestHost.Run(() =>
            {
                var dialog = Formats();
                try
                {
                    var list = Part<ListBox>(dialog, "FormatList");
                    var more = Part<MoreRow>(dialog, "FormatMore");
                    Assert.False(more.IsExpanded);
                    list.SelectedIndex = 1;
                    Assert.True(more.IsExpanded);
                    list.SelectedIndex = 0;
                    Assert.True(more.IsExpanded);

                    // Under More: the key row, for a JSON format alone, and
                    // the gear values on a line of their own.
                    var rows = Assert.IsType<StackPanel>(more.Content);
                    Assert.Equal(2, rows.Children.Count);
                    var key = Assert.IsType<SettingRow>(rows.Children[0]);
                    Assert.Equal("Key", System.Windows.Data.BindingOperations.GetBinding(
                        key.Children.OfType<TextBox>().Single(), TextBox.TextProperty).Path.Path);
                    var gears = Assert.IsType<FlexLinePanel>(rows.Children[1]);
                    Assert.Same(Part<FlexLinePanel>(dialog, "GearLine"), gears);
                    var use = Logical<CheckBox>(gears).Single();
                    Assert.Same(dialog.FindResource("SwitchCheckBox"), use.Style);
                    Assert.Equal(new[] { "GearReverse", "GearNeutral" }, Logical<Wpf.Ui.Controls.NumberBox>(gears).Select(box =>
                        System.Windows.Data.BindingOperations.GetBinding(box, Wpf.Ui.Controls.NumberBox.ValueProperty).Path.Path));
                }
                finally { dialog.Close(); }
            });
        }

        [Fact]
        public void TheByteOrdersAreLittleEndianThenBigEndian()
        {
            Assert.Equal(new[] { false, true }, TelemetryChoices.ByteOrders.Select(choice => choice.Value));
            Assert.Equal(new[] { Strings.Instance.Telemetry_Formats_LittleEndian, Strings.Instance.Telemetry_Formats_BigEndian },
                TelemetryChoices.ByteOrders.Select(choice => choice.Display));
        }

        /// <summary>The field rows run in the board's order: the name, what
        /// it reads into, the offset and the type (or one JSON key across
        /// both), the scale, the least and the most, then the X that removes
        /// the row. The heads stand in the rows' own columns.</summary>
        [Fact]
        public void TheFieldColumnsRunNameTargetOffsetTypeScaleMinMax()
        {
            var xaml = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "CustomTelemetryFormatsDialog.xaml"));
            var heads = Assert.Single(xaml.Descendants(), e => (string)e.Attribute(X + "Name") == "FieldHeads");
            var rows = Assert.Single(xaml.Descendants(), e => (string)e.Attribute(X + "Name") == "FieldRows");
            var row = Assert.Single(rows.Descendants(), e => e.Name.LocalName == "Grid");

            static string[] Columns(XElement grid) => grid.Elements().Single(e => e.Name.LocalName == "Grid.ColumnDefinitions")
                .Elements().Select(c => (string)c.Attribute("Width")).ToArray();
            static int Column(XElement e) => (int?)e.Attribute("Grid.Column") ?? 0;
            static int Span(XElement e) => (int?)e.Attribute("Grid.ColumnSpan") ?? 1;
            static string Parts(XElement grid, Func<XElement, string> what) => string.Join(" ", grid.Elements()
                .Where(e => e.Name.LocalName != "Grid.ColumnDefinitions")
                .OrderBy(Column).ThenBy(Span)
                .Select(e => Column(e) + (Span(e) > 1 ? "+" + Span(e) : "") + ":" + what(e)));

            // Seven columns that share the width, 6 apart, and the 32 of the remove button.
            var widths = Columns(row);
            Assert.Equal(widths, Columns(heads));
            Assert.Equal(new[] { "1.1*", "6", "1.55*", "6", "0.7*", "6", "0.75*", "6", "0.7*", "6", "0.6*", "6", "0.6*", "6", "32" }, widths);

            const string source = ", Source={x:Static strings:Strings.Instance}}";
            Assert.Equal("0:ColName 2:ColTarget 4:ColOffset 4+3:ColKey 6:ColType 8:ColScale 10:ColMin 12:ColMax",
                Parts(heads, e => ((string)e.Attribute("Text")).Replace("{Binding Telemetry_Formats_", "").Replace(source, "")));
            Assert.All(heads.Elements().Where(e => e.Name.LocalName == "TextBlock"),
                e => Assert.Equal("{StaticResource EditorSubHead}", (string)e.Attribute("Style")));

            static string Bound(XElement e)
            {
                string binding = (string)(e.Attribute("Text") ?? e.Attribute("SelectedValue") ?? e.Attribute("Value") ?? e.Attribute("Click")) ?? "";
                return binding.StartsWith("{Binding ", StringComparison.Ordinal)
                    ? binding.Substring("{Binding ".Length).Split(',')[0]
                    : binding;
            }
            Assert.Equal("0:Name 2:Target 4:Offset 4+3:Key 6:Type 8:Scale 10:Min 12:Max 14:RemoveFieldButton_Click", Parts(row, Bound));

            // The offset and the type show for a binary format, the key for a JSON one.
            static string Shown(XElement e) => (string)e.Attribute("Visibility") ?? "";
            Assert.All(row.Elements().Where(e => Bound(e) is "Offset" or "Type"), e => Assert.Contains("IsBinary", Shown(e)));
            Assert.Contains("IsJson", Shown(row.Elements().Single(e => Bound(e) == "Key")));
            var remove = row.Elements().Single(e => Bound(e) == "RemoveFieldButton_Click");
            Assert.Equal("{StaticResource DialogIconButton}", (string)remove.Attribute("Style"));
            Assert.Equal("LeftToRight", (string)remove.Elements().Single(e => e.Name.LocalName == "Path").Attribute("FlowDirection"));
        }

        // ── The keyboard screen and Screen Overlay dialog ──

        internal static KeyboardScreenDialog Screen(KeyboardScreenInfo screen, KeyboardScreenData current, Size? window = null, string profile = "A Game")
        {
            var dialog = (KeyboardScreenDialog)typeof(KeyboardScreenDialog).GetConstructors(Own)[0].Invoke(new object[]
            {
                new KeyboardScreenService(new SettingsViewModel(), null), screen, current, profile, null, window ?? new Size(320, 240),
            });
            dialog.ShowInTaskbar = false;
            return dialog;
        }

        /// <summary>A screen is drawn as large as fits the board's 300 by 190
        /// box with 7 of the box around it, its proportions kept.</summary>
        [Theory]
        [InlineData(160, 43)]
        [InlineData(320, 240)]
        [InlineData(480, 320)]
        [InlineData(1920, 1080)]
        [InlineData(240, 800)]
        public void ThePreviewFitsTheBoardsBoxWithItsShapeKept(double width, double height)
        {
            var size = KeyboardScreenDialog.PreviewSize(width, height);
            Assert.True(size.Width <= 286.0001 && size.Height <= 176.0001, size.ToString());
            Assert.True(Math.Abs(size.Width - 286) < 0.001 || Math.Abs(size.Height - 176) < 0.001, size.ToString());
            Assert.Equal(width / height, size.Width / size.Height, 6);
            Assert.Equal(new Size(0, 0), KeyboardScreenDialog.PreviewSize(0, height));
            Assert.Equal(new Size(0, 0), KeyboardScreenDialog.PreviewSize(width, double.NaN));
        }

        /// <summary>The dialog is titled with the screen's name and says that
        /// nothing changes before Save. The preview stands at the left in
        /// the board's box, and at the right what the screen shows, then
        /// what that choice adds: a chosen picture adds Choose Picture, and
        /// on an overlay its zoom and, once zoomed, its pan. Lightbar Color
        /// adds the slot an overlay follows. Every setting keeps its reset.</summary>
        [Fact]
        public void TheScreenDialogShowsWhatItsChoiceAdds()
        {
            DialogTestHost.Run(() =>
            {
                var s = Strings.Instance;
                var dialog = Screen(KeyboardScreenInfo.Overlay(1), new KeyboardScreenData { Mode = KeyboardScreenMode.Clock });
                try
                {
                    Assert.Equal("Screen Overlay 1", dialog.Title);
                    Assert.Equal(s.KeyboardScreen_Subtitle, dialog.Subtitle);
                    Assert.False(dialog.IsWide);
                    Assert.False(dialog.ShowClose);
                    Assert.Equal(700, dialog.Width);
                    Assert.Null(dialog.FindName("TitleText"));
                    Assert.Null(dialog.FindName("DetailText"));

                    // The preview: the board's box, with the overlay drawn at
                    // its window's shape inside it.
                    var frame = Part<Border>(dialog, "PreviewFrame");
                    Assert.Equal(300, frame.Width);
                    Assert.Equal(190, frame.Height);
                    Assert.Equal(new CornerRadius(8), frame.CornerRadius);
                    Assert.Equal(0, Grid.GetColumn(frame));
                    Assert.Equal(new GridLength(300), ((Grid)frame.Parent).ColumnDefinitions[0].Width);
                    var drawn = Part<FrameworkElement>(dialog, "OverlayPreview");
                    Assert.Equal(Visibility.Visible, drawn.Visibility);
                    Assert.Equal(Visibility.Collapsed, Part<UIElement>(dialog, "PreviewImage").Visibility);
                    Assert.Equal(176, drawn.Height, 3);
                    Assert.Equal(176 * 320.0 / 240, drawn.Width, 3);

                    // The choices, in the enum's order, so a mode's place is its value.
                    var mode = Part<ComboBox>(dialog, "ModeBox");
                    Assert.Equal(Enum.GetValues<KeyboardScreenMode>(), mode.Items.Cast<KeyboardScreenDialog.Choice<KeyboardScreenMode>>().Select(c => c.Value));
                    Assert.Equal(Enum.GetValues<KeyboardScreenMode>().Select(KeyboardScreenService.ModeName),
                        mode.Items.Cast<object>().Select(item => item.ToString()));
                    Assert.Equal((int)KeyboardScreenMode.Clock, mode.SelectedIndex);
                    Assert.True(ComboBoxWidthBehavior.GetSizeToItems(mode));
                    Assert.Equal(200, mode.MaxWidth);
                    Assert.Equal(2, Grid.GetColumn((UIElement)((FrameworkElement)mode.Parent).Parent));

                    // What follows the picker, in order.
                    var column = (StackPanel)((FrameworkElement)mode.Parent).Parent;
                    var hint = Part<TextBlock>(dialog, "HintText");
                    var picture = Part<SettingRow>(dialog, "PictureRow");
                    var profile = Part<StackPanel>(dialog, "ProfilePicturePanel");
                    var slotRow = Part<SettingRow>(dialog, "LightbarSlotRow");
                    var zoom = Part<SettingRow>(dialog, "ZoomRow");
                    var pan = Part<SettingRow>(dialog, "PanRow");
                    Assert.Equal(new UIElement[] { (UIElement)mode.Parent, hint, picture, profile, slotRow, zoom, pan }, column.Children.Cast<UIElement>());
                    Assert.Same(dialog.FindResource("DialogNote"), hint.Style);
                    Assert.Equal(s.KeyboardScreen_Hint_Clock_Overlay, hint.Text);

                    void Shows(params UIElement[] shown)
                    {
                        foreach (var row in new UIElement[] { picture, profile, slotRow, zoom, pan })
                            Assert.True((row.Visibility == Visibility.Visible) == shown.Contains(row),
                                ((FrameworkElement)row).Name + " in " + (KeyboardScreenMode)mode.SelectedIndex);
                    }

                    Shows();
                    mode.SelectedIndex = (int)KeyboardScreenMode.Picture;
                    Shows(picture);
                    mode.SelectedIndex = (int)KeyboardScreenMode.ProfilePicture;
                    Shows(profile);
                    Assert.Equal(Visibility.Collapsed, Part<UIElement>(dialog, "RemoveProfilePictureButton").Visibility);
                    mode.SelectedIndex = (int)KeyboardScreenMode.Lightbar;
                    Shows(slotRow);

                    // The slot: the lowest one with a color first, then each slot by its number.
                    var slot = Part<ComboBox>(dialog, "LightbarSlotBox");
                    var slots = slot.Items.Cast<KeyboardScreenDialog.Choice<int>>().ToList();
                    Assert.Equal(InputManager.MaxPads + 1, slots.Count);
                    Assert.Equal(new KeyboardScreenDialog.Choice<int>(KeyboardScreenData.LowestSlot, s.KeyboardScreen_LightbarSlot_Lowest), slots[0]);
                    Assert.Equal(new KeyboardScreenDialog.Choice<int>(0, 1.ToString(System.Globalization.CultureInfo.CurrentCulture)), slots[1]);
                    Assert.Equal(InputManager.MaxPads - 1, slots[^1].Value);
                    Assert.Equal(0, slot.SelectedIndex);
                    Assert.Equal("ScreenOverlay_LightbarSlotBox", AutomationProperties.GetAutomationId(slot));
                    Assert.Equal("ScreenOverlay_ZoomBox", AutomationProperties.GetAutomationId(Part<TextBox>(dialog, "ZoomBox")));

                    // The numbers: 72 wide with their digits at the right. The
                    // pan's first box starts where the rows above start theirs.
                    foreach (string box in new[] { "ZoomBox", "PanXBox", "PanYBox" })
                    {
                        Assert.Equal(72, Part<TextBox>(dialog, box).Width);
                        Assert.Equal(TextAlignment.Right, Part<TextBox>(dialog, box).TextAlignment);
                    }
                    var panParts = pan.Children.Cast<FrameworkElement>().ToList();
                    Assert.Equal(100, panParts[0].Width + panParts[0].Margin.Right + panParts[1].Width + panParts[1].Margin.Right);

                    Assert.Equal(new[]
                    {
                        "Reset_?dialog_KeyboardScreenModeBox_13110", "Reset_?dialog_KeyboardScreenPicture_13111",
                        "Reset_?dialog_KeyboardScreenLightbarSlot_55711", "Reset_?dialog_KeyboardScreenZoom_55710",
                        "Reset_?dialog_KeyboardScreenPan_13112",
                    }, ResetIds(dialog));

                    // The foot: Cancel, then Save.
                    var foot = Assert.IsType<FlexLinePanel>(dialog.Foot);
                    Assert.True(Assert.IsType<Button>(foot.Children[0]).IsCancel);
                    Assert.Same(Part<Button>(dialog, "SaveButton"), foot.Children[1]);
                    Assert.True(Part<Button>(dialog, "SaveButton").IsDefault);
                }
                finally { dialog.Close(); }

                // A keyboard's own screen: its picture at the screen's shape,
                // and no slot, since it follows the slots its Devices row feeds.
                var mono = Screen(new KeyboardScreenInfo("test-mono", KeyboardScreenKind.Monochrome, "G15", "G15"),
                    new KeyboardScreenData { Mode = KeyboardScreenMode.Lightbar });
                try
                {
                    var image = Part<Image>(mono, "PreviewImage");
                    Assert.Equal(Visibility.Visible, image.Visibility);
                    Assert.Equal(Visibility.Collapsed, Part<UIElement>(mono, "OverlayPreview").Visibility);
                    Assert.Equal(286, image.Width, 3);
                    Assert.Equal(286 * 43.0 / 160, image.Height, 3);
                    Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image));
                    Assert.NotNull(image.Source);
                    Assert.Equal(Visibility.Collapsed, Part<UIElement>(mono, "LightbarSlotRow").Visibility);
                    Assert.Equal(s.KeyboardScreen_Hint_Lightbar, Part<TextBlock>(mono, "HintText").Text);
                }
                finally { mono.Close(); }
            });
        }

        // ── Dreamcast Screen ──

        internal static DreamcastScreenDialog Dreamcast(BlissBoxPort port, BlissBoxPortData current, string profile = "A Game")
        {
            var dialog = (DreamcastScreenDialog)typeof(DreamcastScreenDialog).GetConstructors(Own)[0].Invoke(new object[]
            {
                new DreamcastScreenService(new SettingsViewModel(), null), port, current, profile, null,
            });
            dialog.ShowInTaskbar = false;
            return dialog;
        }

        /// <summary>Dreamcast Screen: the VMU at four screen pixels to each
        /// of its own in the board's box, the picker filling its row, and
        /// the picture buttons the mode takes.</summary>
        [Fact]
        public void TheDreamcastDialogShowsWhatItsChoiceAdds()
        {
            DialogTestHost.Run(() =>
            {
                var s = Strings.Instance;
                using var port = new BlissBoxPort(@"\\?\hid#padforge-screen-dialog-test", 0x0D04, Guid.NewGuid(), 1);
                var dialog = Dreamcast(port, null);
                try
                {
                    Assert.Equal(s.DreamcastScreen_Title, dialog.Title);
                    Assert.Equal(s.DreamcastScreen_Subtitle, dialog.Subtitle);
                    Assert.False(dialog.IsWide);
                    Assert.Equal(560, dialog.Width);
                    Assert.Null(dialog.FindName("DetailText"));

                    var frame = Part<Border>(dialog, "PreviewFrame");
                    Assert.Equal(194, frame.Width);
                    Assert.Equal(130, frame.Height);
                    Assert.Equal(new CornerRadius(6), frame.CornerRadius);
                    Assert.Equal(new GridLength(194), ((Grid)frame.Parent).ColumnDefinitions[0].Width);
                    var image = Part<Image>(dialog, "PreviewImage");
                    Assert.Same(image, frame.Child);
                    Assert.Equal(192, image.Width);
                    Assert.Equal(128, image.Height);
                    Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image));
                    var clip = Assert.IsType<RectangleGeometry>(image.Clip);
                    Assert.Equal(new Rect(0, 0, 192, 128), clip.Rect);
                    Assert.Equal(5, clip.RadiusX);
                    Assert.NotNull(image.Source);

                    var mode = Part<ComboBox>(dialog, "ModeBox");
                    Assert.Equal(Enum.GetValues<DreamcastScreenMode>().Length, mode.Items.Count);
                    Assert.Equal((int)DreamcastScreenMode.Adapter, mode.SelectedIndex);
                    Assert.True(SettingRow.GetFills(mode));
                    var picture = Part<SettingRow>(dialog, "PictureRow");
                    var profile = Part<StackPanel>(dialog, "ProfilePicturePanel");
                    var hint = Part<TextBlock>(dialog, "HintText");
                    var column = (StackPanel)((FrameworkElement)mode.Parent).Parent;
                    Assert.Equal(new UIElement[] { (UIElement)mode.Parent, hint, picture, profile }, column.Children.Cast<UIElement>());
                    Assert.Equal(s.DreamcastScreen_Hint_Adapter, hint.Text);
                    Assert.Equal(Visibility.Collapsed, picture.Visibility);
                    Assert.Equal(Visibility.Collapsed, profile.Visibility);
                    mode.SelectedIndex = (int)DreamcastScreenMode.Picture;
                    Assert.Equal(Visibility.Visible, picture.Visibility);
                    Assert.Equal(Visibility.Collapsed, profile.Visibility);
                    mode.SelectedIndex = (int)DreamcastScreenMode.ProfilePicture;
                    Assert.Equal(Visibility.Collapsed, picture.Visibility);
                    Assert.Equal(Visibility.Visible, profile.Visibility);

                    // A long profile name is cut short in its button and read whole in the tooltip.
                    var choose = Part<Button>(dialog, "ChooseProfilePictureButton");
                    Assert.Equal(TextTrimming.CharacterEllipsis, Assert.IsType<TextBlock>(choose.Content).TextTrimming);
                    Assert.Equal(string.Format(s.DreamcastScreen_ChooseProfilePicture_Format, "A Game"), choose.ToolTip);

                    Assert.Equal(new[] { "Reset_?dialog_ModeBox_48", "Reset_?dialog_ChoosePictureButton_63" }, ResetIds(dialog));
                    var foot = Assert.IsType<FlexLinePanel>(dialog.Foot);
                    Assert.True(Assert.IsType<Button>(foot.Children[0]).IsCancel);
                    Assert.True(Assert.IsType<Button>(foot.Children[1]).IsDefault);
                }
                finally { dialog.Close(); }
            });
        }

        // ── Map Key Lights ──

        /// <summary>A sentence with two values comes apart around them, in
        /// the order the sentence puts them, so each value can stand in a
        /// run of its own. A sentence that formats a value is left whole.</summary>
        [Fact]
        public void ASentenceSplitsAroundItsTwoValues()
        {
            Assert.Equal(new[] { "Keys named: ", "3", " of ", "7", "" }, FormatRuns.SplitTwo("Keys named: {0} of {1}", 3, 7));
            Assert.Equal(new[] { "", "7", " keys, ", "3", " named" }, FormatRuns.SplitTwo("{1} keys, {0} named", 3, 7));
            Assert.Equal(new[] { "{", "3", "} of ", "7", "." }, FormatRuns.SplitTwo("{{{0}}} of {1}.", 3, 7));
            Assert.Equal(new[] { "3 of 7", "", "", "", "" }, FormatRuns.SplitTwo("{0:N0} of {1}", 3, 7));
            Assert.Equal(new[] { "No numbers here.", "", "", "", "" }, FormatRuns.SplitTwo("No numbers here.", 3, 7));
            foreach (string format in new[] { "Keys named: {0} of {1}", "{1} keys, {0} named", Strings.Instance.MapKeyLights_Progress_Format })
                Assert.Equal(string.Format(format, 12, 104), string.Concat(FormatRuns.SplitTwo(format, 12, 104)));
        }

        /// <summary>Map Key Lights: the board's line under the title, the
        /// device with its progress read whole, the picture in its box, the
        /// buttons that step through the lights, then the lines that say
        /// what to do. The foot leads with Export, Import and Forget All
        /// and ends with Close. The head has no X.</summary>
        [Fact]
        public void MapKeyLightsShowsTheBoardsParts()
        {
            DialogTestHost.Run(() =>
            {
                var s = Strings.Instance;
                // A device with no lights published: the dialog reads no map of another test's.
                var dialog = new MapKeyLightsDialog(new OutputPath(OutputFamily.HidLightingUnit, "screen-dialog-test-" + Guid.NewGuid().ToString("N")), "Bench Keyboard");
                try
                {
                    Lay(dialog);
                    var vm = Assert.IsType<MapKeyLightsViewModel>(dialog.DataContext);
                    Assert.Equal(s.MapKeyLights_Title, dialog.Title);
                    Assert.Equal(s.MapKeyLights_Intro, dialog.Subtitle);
                    Assert.False(dialog.ShowClose);
                    Assert.Equal(820, dialog.Width);

                    // The progress: five runs that make the sentence, its two
                    // numbers in the telemetry face, read whole by its name.
                    var progress = Part<TextBlock>(dialog, "ProgressText");
                    var runs = progress.Inlines.OfType<Run>().ToList();
                    Assert.Equal(5, runs.Count);
                    Assert.Equal(vm.Progress, string.Concat(runs.Select(run => run.Text)));
                    Assert.Equal(vm.Progress, AutomationProperties.GetName(progress));
                    Assert.Equal(new[] { vm.ProgressLead, vm.ProgressFirst, vm.ProgressMiddle, vm.ProgressSecond, vm.ProgressTail }, runs.Select(run => run.Text));
                    Assert.Equal("0", vm.ProgressFirst);

                    // The body, top to bottom.
                    var body = Assert.IsType<StackPanel>(dialog.Content);
                    var box = Part<Border>(dialog, "PictureBox");
                    var steps = Part<FlexLinePanel>(dialog, "StepButtons");
                    Assert.IsType<FlexLinePanel>(body.Children[0]);
                    Assert.Same(box, body.Children[1]);
                    Assert.IsType<KeyLightsView>(box.Child);
                    Assert.Equal(new Thickness(14), box.Padding);
                    Assert.Same(steps, body.Children[2]);
                    var status = Assert.IsType<TextBlock>(body.Children[3]);
                    Assert.True(LiveText.GetPolite(status));
                    Assert.Equal(s.MapKeyLights_NoLights, status.Text);
                    Assert.Equal(s.MapKeyLights_NoLightHint, Assert.IsType<TextBlock>(body.Children[4]).Text);
                    var note = Part<TextBlock>(dialog, "NoteText");
                    Assert.Same(note, body.Children[5]);
                    Assert.True(LiveText.GetPolite(note));
                    Assert.Equal(Visibility.Collapsed, note.Visibility);

                    // Start, Back, Skip, Forget, Stop. With nothing to learn
                    // none can be used.
                    var buttons = steps.Children.Cast<Button>().ToList();
                    Assert.Equal(new object[] { s.MapKeyLights_Start, s.MapKeyLights_Back, s.MapKeyLights_Skip, s.MapKeyLights_Forget, s.MapKeyLights_Stop },
                        buttons.Select(button => button.Content));
                    Assert.Same(Part<Button>(dialog, "StartBtn"), buttons[0]);
                    Assert.Same(dialog.FindResource("DialogPrimaryButton"), buttons[0].Style);
                    Assert.All(buttons, button => Assert.False(button.IsEnabled));

                    // The foot.
                    var foot = Assert.IsType<FlexLinePanel>(dialog.Foot).Children.Cast<Button>().ToList();
                    Assert.Equal(new object[] { s.MapKeyLights_Export, s.MapKeyLights_Import, s.MapKeyLights_ForgetAll, s.Common_Close },
                        foot.Select(button => button.Content));
                    Assert.Same(dialog.FindResource("DialogQuietButton"), foot[0].Style);
                    Assert.Same(dialog.FindResource("DialogQuietButton"), foot[1].Style);
                    Assert.Same(dialog.FindResource("DialogQuietDangerButton"), foot[2].Style);
                    Assert.Equal(s.MapKeyLights_ForgetAll_Tooltip, foot[2].ToolTip);
                    Assert.True(foot[3].IsCancel);
                    Assert.Equal(new[] { false, false, false, true }, foot.Select(FlexLinePanel.GetAtEnd));
                }
                finally { dialog.Close(); }
            });
        }

        // ── The icon picker ──

        /// <summary>The picker wears the dialogs' look in a popup: the board's
        /// 420 with corners of 12, a switch between Emoji and Images, tiles
        /// eight to a row, and a foot that clears the icon or browses, from
        /// either tab. It opens on the tab and the category that hold the
        /// icon held now, which is marked, and the switch follows the keys
        /// as well as a click.</summary>
        [Fact]
        public void TheIconPickerOpensOnWhatHoldsTheIcon()
        {
            DialogTestHost.Run(() =>
            {
                var picker = new IconPicker();
                picker.Prepare("", "No Icon");
                picker.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                picker.Arrange(new Rect(picker.DesiredSize));
                picker.UpdateLayout();

                var frame = Assert.IsType<Border>(picker.Content);
                Assert.Equal(420, frame.Width);
                Assert.Equal(new CornerRadius(12), frame.CornerRadius);
                Assert.Equal(Strings.Instance.Menu_Icon_PickTitle, AutomationProperties.GetName(picker));

                var emojiTab = Part<RadioButton>(picker, "EmojiTabButton");
                var imagesTab = Part<RadioButton>(picker, "ImagesTabButton");
                var emojiPanel = Part<UIElement>(picker, "EmojiPanel");
                var imagesPanel = Part<UIElement>(picker, "ImagesPanel");
                Assert.Equal(emojiTab.GroupName, imagesTab.GroupName);
                Assert.Same(picker.FindResource("SegmentRadio"), emojiTab.Style);
                Assert.Same(picker.FindResource("SegmentTrack"), ((FrameworkElement)((FrameworkElement)emojiTab.Parent).Parent).Style);
                Assert.True(emojiTab.IsChecked);
                Assert.False(imagesTab.IsChecked);
                Assert.Equal(Visibility.Visible, emojiPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, imagesPanel.Visibility);

                // The switch moves the panels, both ways.
                imagesTab.IsChecked = true;
                Assert.False(emojiTab.IsChecked);
                Assert.Equal(Visibility.Collapsed, emojiPanel.Visibility);
                Assert.Equal(Visibility.Visible, imagesPanel.Visibility);
                emojiTab.IsChecked = true;
                Assert.False(imagesTab.IsChecked);
                Assert.Equal(Visibility.Visible, emojiPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, imagesPanel.Visibility);

                // The foot holds the clear button, with the caller's words,
                // and Browse, outside both tabs.
                var foot = Part<Border>(picker, "PickerFoot");
                var clear = Part<Button>(picker, "ClearButton");
                var browse = Part<Button>(picker, "BrowseButton");
                Assert.Equal(new UIElement[] { clear, browse }, Assert.IsType<FlexLinePanel>(foot.Child).Children.Cast<UIElement>());
                Assert.Equal("No Icon", clear.Content);
                Assert.Same(picker.FindResource("DialogQuietButton"), clear.Style);
                Assert.Equal(Strings.Instance.Macro_Sound_BrowseFiles, browse.Content);
                Assert.DoesNotContain(Logical<Button>(Part<FrameworkElement>(picker, "PickerBody")), button => button == clear || button == browse);
                int browsed = 0, chosen = 0;
                picker.BrowseRequested += () => browsed++;
                picker.IconChosen += reference => { Assert.Equal("", reference); chosen++; };
                browse.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                clear.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(1, browsed);
                Assert.Equal(1, chosen);

                // Emoji: the first category is the one shown, and its tiles
                // stand eight to a row, 40 wide, 6 apart.
                var catalog = ShiftActivatorDialog.EmojiCatalog;
                var categories = Part<ItemsControl>(picker, "EmojiCategoryBar").Items.Cast<IconPicker.CategoryChoice>().ToList();
                Assert.Equal(catalog, categories.Select(c => c.Category));
                Assert.Equal(catalog[0], Assert.Single(categories, c => c.IsCurrent).Category);
                var grid = Part<ItemsControl>(picker, "EmojiGrid");
                Assert.Equal(catalog[0].Emojis, grid.Items.Cast<IconPicker.EmojiChoice>().Select(e => e.Glyph));
                Assert.DoesNotContain(grid.Items.Cast<IconPicker.EmojiChoice>(), e => e.IsCurrent);
                var tiles = Assert.Single(Visual<TileGridPanel>(grid));
                Assert.Equal(40, tiles.MinItemWidth);
                Assert.Equal(6, tiles.Gap);
                Assert.Equal(8, tiles.MaxColumns);
                var radios = Visual<RadioButton>(Part<ItemsControl>(picker, "EmojiCategoryBar")).ToList();
                Assert.Equal(catalog.Length, radios.Count);
                Assert.True(radios[0].IsChecked);
                Assert.Single(radios.Select(r => r.GroupName).Distinct());
                Assert.NotEqual(emojiTab.GroupName, radios[0].GroupName);

                // Another category shows its own emoji.
                radios[2].IsChecked = true;
                Assert.False(radios[0].IsChecked);
                Assert.Equal(catalog[2].Emojis, grid.Items.Cast<IconPicker.EmojiChoice>().Select(e => e.Glyph));

                // Opened with an emoji held: its category shows, and its tile is marked.
                string held = catalog[5].Emojis[3];
                var again = new IconPicker();
                again.Prepare(held, "Reset");
                Assert.Equal(catalog[5], Assert.Single(Part<ItemsControl>(again, "EmojiCategoryBar").Items.Cast<IconPicker.CategoryChoice>(), c => c.IsCurrent).Category);
                Assert.Equal(held, Assert.Single(Part<ItemsControl>(again, "EmojiGrid").Items.Cast<IconPicker.EmojiChoice>(), e => e.IsCurrent).Glyph);
                Assert.True(Part<RadioButton>(again, "EmojiTabButton").IsChecked);

                // Opened with a picture held: the Images tab.
                var images = new IconPicker();
                images.Prepare("pficon://No Such Package/a.png", "No Icon");
                Assert.True(Part<RadioButton>(images, "ImagesTabButton").IsChecked);
                Assert.Equal(Visibility.Visible, Part<UIElement>(images, "ImagesPanel").Visibility);
                Assert.Equal(Visibility.Collapsed, Part<UIElement>(images, "EmojiPanel").Visibility);
            });

            // The old tab and choice styles are gone, with the code that painted a tab.
            string xaml = ExtrasPageTests.RepoText("PadForge.App", "Views", "IconPicker.xaml");
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "IconPicker.xaml.cs");
            Assert.DoesNotContain("x:Key=\"PickerTab\"", xaml);
            Assert.DoesNotContain("x:Key=\"PickerChoice\"", xaml);
            Assert.DoesNotContain("MarkTab", code);
            // The dialogs' styles are merged before the markup that names them is read.
            int merged = code.IndexOf("Resources.MergedDictionaries.Add(DialogWindow.Looks);", StringComparison.Ordinal);
            Assert.True(merged > 0);
            Assert.True(merged < code.IndexOf("InitializeComponent();", StringComparison.Ordinal));
        }
    }
}
