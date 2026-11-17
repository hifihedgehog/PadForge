using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using PadForge.Controls;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The Mappings list and its open row, as the approved "keep the frame"
    /// canvas draws them. A closed row is a line of cells, one under each
    /// device that drives the controller. An open row shows its sources
    /// first, one line each and all alike, then its output. The bar under
    /// the picture shows the same lines for the picked control.
    ///
    /// <para>The sheet's rules, each held here: every source is one line
    /// with the same parts in the same order (Device, Mode, Source with
    /// Record and Clear, Invert, Half, Bidirectional, Remove). A mode's
    /// settings stand on one line under its source. A mode that reads keys
    /// shows them where the input stands, with one Record and Clear and no
    /// switches. The output stands last. Letters show only with Custom.
    /// Every setting keeps its reset, and a switch that cannot be set is
    /// dimmed. A narrow box moves the three switches under the source with
    /// their names.</para>
    /// </summary>
    public class PadMappingLinesTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string S = ", Source={x:Static strings:Strings.Instance}}";
        private const string TwoWay = ", Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}";
        private const string Shown = ", Converter={StaticResource BoolToVisibilityConverter}}";
        /// <summary>The rule of a part that a mode reading keys of its own does not show.</summary>
        private const string NoKeys = "{Binding IsLineKeyMode, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}";

        private static XDocument Page() => XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));

        private static XElement Keyed(XDocument page, string key)
            => Assert.Single(page.Root.Elements().Single(e => e.Name.LocalName == "UserControl.Resources").Elements(),
                e => (string)e.Attribute(X + "Key") == key);

        private static XElement Named(XContainer scope, string name)
            => Assert.Single(scope.Descendants(), e => (string)e.Attribute(X + "Name") == name);

        private static string Name(XElement e) => (string)e.Attribute(X + "Name");

        /// <summary>An attached property's value, whatever prefix names its owner.</summary>
        private static string Attached(XElement e, string property)
            => (string)e.Attributes().SingleOrDefault(a => a.Name.LocalName == property);

        private static string Part(XElement e) => Attached(e, "SourceLinePanel.Part");

        private static string Id(XElement e) => Attached(e, "AutomationProperties.AutomationId");

        /// <summary>An element's children that are elements of the page, and no property of it.</summary>
        private static List<XElement> Kids(XElement e) => e.Elements().Where(c => !c.Name.LocalName.Contains('.')).ToList();

        private static List<XElement> TriggersOf(XElement template)
            => template.Elements().Single(e => e.Name.LocalName.EndsWith(".Triggers", StringComparison.Ordinal)).Elements().ToList();

        private static (string Target, string Property, string Value)[] Setters(XElement trigger)
            => trigger.Elements().Where(e => e.Name.LocalName == "Setter")
                .Select(s => ((string)s.Attribute("TargetName"), (string)s.Attribute("Property"), (string)s.Attribute("Value"))).ToArray();

        // ── the open row ───────────────────────────────────────────────

        /// <summary>An open row is its sources, Add Source and its output,
        /// in that order, under a head that names each column once. The
        /// row's first source and each added one are drawn by the one
        /// line, so a source reads the same wherever it stands. The list's
        /// open row and the bar under the picture show the one panel.</summary>
        [Fact]
        public void AnOpenRowIsItsSourcesThenAddSourceThenItsOutput()
        {
            var page = Page();
            var panel = Keyed(page, "MappingRowPanel");
            var box = Named(panel, "RowBox");
            Assert.Equal("SourceBox", box.Name.LocalName);
            // The letters show where the row's formula does.
            Assert.Equal("{Binding ShouldShowCustomExpression}", Attached(box, "SourceLinePanel.Letters"));

            var parts = Kids(Assert.Single(Kids(box)));
            Assert.Equal(new[] { "SourceLinePanel", "ContentControl", "ItemsControl", "Border", "Border" }, parts.Select(e => e.Name.LocalName));
            Assert.Equal(new[] { null, "FirstSource", "AddedSources", null, "OutputZone" }, parts.Select(Name));

            // The head: a name over each column, the three switches' centered.
            var heads = Kids(parts[0]);
            Assert.Equal("30", (string)parts[0].Attribute("LineMin"));
            Assert.Equal(new[] { "Device", "Mode", "Source", "Invert", "Half", "Either" }, heads.Select(Part));
            Assert.Equal(new[] { "Pad_Row_Device", "Pad_Row_Mode", "Pad_HeaderSource", "Pad_Invert", "Pad_Half", "Pad_Either" }.Select(k => "{Binding " + k + S),
                heads.Select(h => (string)h.Attribute("Text")));
            Assert.Equal(new[] { "LineHead", "LineHead", "LineHead", "LineSwitchHead", "LineSwitchHead", "LineSwitchHead" }.Select(k => "{StaticResource " + k + "}"),
                heads.Select(h => (string)h.Attribute("Style")));
            var s = Strings.Instance;
            Assert.Equal(new[] { "Device", "Mode" }, new[] { s.Pad_Row_Device, s.Pad_Row_Mode });

            // The sources: the first is the row itself, the rest its list.
            Assert.Equal("{Binding}", (string)parts[1].Attribute("Content"));
            Assert.Equal("{StaticResource SourceLine}", (string)parts[1].Attribute("ContentTemplate"));
            Assert.Equal("{Binding ExtraSources}", (string)parts[2].Attribute("ItemsSource"));
            Assert.Equal("{StaticResource SourceLine}", (string)parts[2].Attribute("ItemTemplate"));
            Assert.True(typeof(IMappingSourceLine).IsAssignableFrom(typeof(MappingItem)));
            Assert.True(typeof(IMappingSourceLine).IsAssignableFrom(typeof(MappingSourceItem)));

            // Add Source, and beside it the offer to map a stick's other direction.
            var add = Named(parts[3], "AddSource");
            Assert.Equal("{Binding AddExtraSourceCommand}", (string)add.Attribute("Command"));
            Assert.Equal("{StaticResource QuietTextButton}", (string)add.Attribute("Style"));
            Assert.Equal("{Binding Pad_Mapping_AddSource" + S, (string)add.Attribute("AutomationProperties.Name"));
            Assert.Contains(add.Descendants(), e => (string)e.Attribute("Text") == "{Binding Pad_Mapping_AddSource" + S);
            var other = Assert.Single(parts[3].Descendants(), e => (string)e.Attribute("Command") == "{Binding AddOppositeDirectionCommand}");
            Assert.Equal("{Binding ShouldShowEmptyDirectionHint" + Shown, (string)other.Attribute("Visibility"));

            // One panel for the list and for the picture's bar.
            var details = Assert.Single(page.Descendants(), e => e.Name.LocalName == "DataGrid.RowDetailsTemplate");
            Assert.Equal("{StaticResource MappingRowPanel}", (string)Assert.Single(details.Descendants(), e => e.Name.LocalName == "ContentControl").Attribute("ContentTemplate"));
            var lines = Named(page, "PickedLines");
            var host = Assert.Single(lines.Elements(), e => e.Name.LocalName == "ContentControl");
            Assert.Equal("{Binding PickedRow}", (string)host.Attribute("Content"));
            // The template is the host's only while it has a row: set outright,
            // it is built for no row too, and its lookups through the row find
            // nothing.
            Assert.Null(host.Attribute("ContentTemplate"));
            var hostStyle = Assert.Single(host.Descendants(), e => e.Name.LocalName == "Style");
            Assert.Equal("{StaticResource MappingRowPanel}", (string)Assert.Single(hostStyle.Elements(),
                e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == "ContentTemplate").Attribute("Value"));
            var noRow = Assert.Single(hostStyle.Descendants(), e => e.Name.LocalName == "Trigger");
            Assert.Equal(("Content", "{x:Null}"), ((string)noRow.Attribute("Property"), (string)noRow.Attribute("Value")));
            Assert.Equal("{x:Null}", (string)Assert.Single(noRow.Elements()).Attribute("Value"));
        }

        /// <summary>A source's line: the letter a formula knows it by, its
        /// device, its mode with the mode's reset, its input with Record
        /// and Clear beside it, the three switches with a reset each, and
        /// Remove. A switch that cannot be set is dimmed, never hidden.</summary>
        [Fact]
        public void ASourcesLineIsItsDeviceItsModeItsInputTheSwitchesAndRemove()
        {
            var page = Page();
            var line = Keyed(page, "SourceLine");
            var parts = Kids(Assert.Single(line.Descendants(), e => e.Name.LocalName == "SourceLinePanel"));
            Assert.Equal(new[] { "Letter", "Device", "Mode", "Source", "Invert", "Half", "Either", "Remove", "Under" }, parts.Select(Part));

            Assert.Equal("{Binding LineLetter}", (string)parts[0].Attribute("Text"));

            // The device: its light, its kind, its name, and that it is away.
            var device = parts[1];
            Assert.StartsWith("{Binding LineDeviceLabel,", (string)device.Attribute("AutomationProperties.Name"));
            Assert.Equal("LeftToRight", (string)Named(device, "DeviceLight").Attribute("FlowDirection"));
            Assert.StartsWith("{Binding LineDevice.TypeGlyph,", (string)Named(device, "DeviceKind").Attribute("Text"));
            Assert.Equal("{Binding LineDeviceLabel}", (string)Named(device, "DeviceName").Attribute("Text"));
            var away = Named(device, "AwayNote");
            Assert.Equal("{Binding Home_Device_NotConnected" + S, (string)away.Attribute("Text"));
            Assert.Equal("Collapsed", (string)away.Attribute("Visibility"));
            // The source's marks: which way it pushes a stick, and the trim stick.
            var marks = device.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource LineBadge}").ToList();
            Assert.Equal(new[] { "{Binding LineDirection, Converter={StaticResource StringToVisibility}}", "{Binding IsTrimStick" + Shown },
                marks.Select(m => (string)m.Attribute("Visibility")));
            Assert.Contains(marks[1].Descendants(), e => (string)e.Attribute("Text") == "{Binding Pad_Row_TrimStick" + S);

            // The mode, on the one that holds it, with its reset after it.
            var mode = parts[2];
            Assert.Equal("{Binding ModeHolder}", (string)mode.Attribute("DataContext"));
            Assert.Equal("Local", Attached(mode, "KeyboardNavigation.TabNavigation"));
            var modeParts = Kids(mode);
            Assert.Equal(new[] { "SettingResetButton", "ComboBox" }, modeParts.Select(e => e.Name.LocalName));
            Assert.Equal(("Reset_MappingSourceItem_Kind_4282", "Kind", "{Binding Pad_Row_Mode" + S, "1"),
                (Id(modeParts[0]), (string)modeParts[0].Attribute("CommandParameter"), (string)modeParts[0].Attribute("SettingLabel"), (string)modeParts[0].Attribute("TabIndex")));
            Assert.Equal("{Binding ModeChoices}", (string)modeParts[1].Attribute("ItemsSource"));
            Assert.Equal("{Binding Kind" + TwoWay, (string)modeParts[1].Attribute("SelectedValue"));
            Assert.Equal(("Value", "Name", "0"), ((string)modeParts[1].Attribute("SelectedValuePath"), (string)modeParts[1].Attribute("DisplayMemberPath"), (string)modeParts[1].Attribute("TabIndex")));
            Assert.Equal("{Binding Pad_Row_Mode" + S, (string)modeParts[1].Attribute("AutomationProperties.Name"));

            // The input, then Record, then Clear, which Tab takes in that order.
            var own = Named(parts[3], "OwnInput");
            Assert.Equal(NoKeys, (string)own.Attribute("Visibility"));
            Assert.Equal("Local", Attached(own, "KeyboardNavigation.TabNavigation"));
            var input = Kids(own);
            Assert.Equal(new[] { "OwnClear", "OwnRecord", null }, input.Select(Name));
            Assert.Equal(new[] { "2", "1", "0" }, input.Select(e => (string)e.Attribute("TabIndex")));
            Assert.Equal("{Binding LineClearCommand}", (string)input[0].Attribute("Command"));
            Assert.Equal("{Binding Common_Clear" + S, (string)input[0].Attribute("AutomationProperties.Name"));
            Assert.Equal(("{Binding LineRecordCommand}", "{StaticResource RecordButton}", "{Binding LineRecordIcon}", "{Binding CanLineRecord}"),
                ((string)input[1].Attribute("Command"), (string)input[1].Attribute("Style"), (string)input[1].Attribute("Content"), (string)input[1].Attribute("IsEnabled")));
            Assert.StartsWith("{Binding LineRecordText,", (string)input[1].Attribute("AutomationProperties.Name"));
            Assert.Equal("ComboBox", input[2].Name.LocalName);
            Assert.Equal("{Binding Row.AvailableInputsView}", (string)input[2].Attribute("ItemsSource"));
            Assert.Equal("{Binding SelectedInput" + TwoWay, (string)input[2].Attribute("SelectedItem"));
            Assert.Equal("140", (string)input[2].Attribute("MinWidth"));
            Assert.Equal("{Binding Pad_HeaderSource" + S, (string)input[2].Attribute("AutomationProperties.Name"));

            // The three switches: a name for a narrow box, the switch, its reset.
            foreach (var (at, name, key, bound, enabled, reset) in new[]
            {
                (4, "InvertPart", "Pad_Invert", "LineInvert", "{Binding IsInvertApplicable}", "Reset_MappingSourceItem_LineInvert_6001"),
                (5, "HalfPart", "Pad_Half", "LineHalf", "{Binding IsHalfAxisApplicable}", "Reset_MappingSourceItem_LineHalf_6002"),
                (6, "EitherPart", "Pad_Either", "LineBidirectional", "{Binding LineHalf}", "Reset_MappingSourceItem_LineBidirectional_6003"),
            })
            {
                Assert.Equal(name, Name(parts[at]));
                // Dimmed where it cannot be set. The one thing that hides it is
                // a mode that reads keys, which has no input to invert or halve.
                Assert.Equal(NoKeys, (string)parts[at].Attribute("Visibility"));
                var three = Kids(parts[at]);
                Assert.Equal(new[] { "TextBlock", "CheckBox", "SettingResetButton" }, three.Select(e => e.Name.LocalName));
                Assert.Equal(("{StaticResource LineSwitchName}", "{Binding " + key + S), ((string)three[0].Attribute("Style"), (string)three[0].Attribute("Text")));
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)three[1].Attribute("Style"));
                Assert.Equal("{Binding " + key + S, (string)three[1].Attribute("Content"));
                Assert.Equal("{Binding " + bound + TwoWay, (string)three[1].Attribute("IsChecked"));
                Assert.Equal(enabled, (string)three[1].Attribute("IsEnabled"));
                Assert.Equal((reset, bound, "{Binding " + key + S), (Id(three[2]), (string)three[2].Attribute("CommandParameter"), (string)three[2].Attribute("SettingLabel")));
                Assert.Contains("ResetSettingCommand", (string)three[2].Attribute("Command"));
            }
            // A switch's name shows in a narrow box only, where no head stands over it.
            var switchName = Keyed(page, "LineSwitchName");
            Assert.Contains(switchName.Elements(), e => (string)e.Attribute("Property") == "Visibility" && (string)e.Attribute("Value") == "Collapsed");
            var narrow = Assert.Single(switchName.Descendants(), e => e.Name.LocalName == "Trigger");
            Assert.Equal(("controls:SourceLinePanel.Narrow", "True"), ((string)narrow.Attribute("Property"), (string)narrow.Attribute("Value")));
            Assert.Equal(("Visibility", "Visible"), ((string)Assert.Single(narrow.Elements()).Attribute("Property"), (string)Assert.Single(narrow.Elements()).Attribute("Value")));
            // The head's names of the three go where the switches' own show.
            var headName = Assert.Single(Keyed(page, "LineSwitchHead").Descendants(), e => e.Name.LocalName == "Trigger");
            Assert.Equal(("controls:SourceLinePanel.Narrow", "True"), ((string)headName.Attribute("Property"), (string)headName.Attribute("Value")));
            Assert.Equal("Collapsed", (string)Assert.Single(headName.Elements()).Attribute("Value"));

            // Remove takes the source off the row.
            Assert.Equal("{Binding LineRemoveCommand}", (string)parts[7].Attribute("Command"));
            Assert.Equal("{Binding Common_Remove" + S, (string)parts[7].Attribute("Content"));
            Assert.Equal("{StaticResource QuietTextButton}", (string)parts[7].Attribute("Style"));

            // What the line shows by the source's state.
            var triggers = TriggersOf(line);
            var sending = Assert.Single(triggers, t => (string)t.Attribute("Binding") == "{Binding LineDevice.IsSending}");
            Assert.Equal(new[] { ("DeviceLight", "Fill", "{DynamicResource ColdBrush}") }, Setters(sending));
            var gone = Assert.Single(triggers, t => (string)t.Attribute("Binding") == "{Binding IsLineAway}");
            Assert.Contains(("AwayNote", "Visibility", "Visible"), Setters(gone));
            Assert.Contains(("DeviceLight", "Fill", "Transparent"), Setters(gone));
            var unset = Assert.Single(triggers, t => (string)t.Attribute("Binding") == "{Binding IsLineUnassigned}");
            Assert.Contains(("DeviceName", "Text", "{Binding Common_Unassigned" + S), Setters(unset));
        }

        /// <summary>A mode that reads keys of its own (Incremental, Ramp,
        /// Invert On Hold) shows them where the input stands: Up and Down,
        /// or the modifier, each with its reset, then one Record that takes
        /// them in turn, and Clear. It has no input of its own, so none of
        /// the three switches shows.</summary>
        [Fact]
        public void AModeThatReadsKeysShowsThemWhereTheInputStands()
        {
            var line = Keyed(Page(), "SourceLine");
            var keys = Named(line, "KeyInputs");
            Assert.Equal("FlexLinePanel", keys.Name.LocalName);
            Assert.Equal("{Binding IsLineKeyMode" + Shown, (string)keys.Attribute("Visibility"));
            Assert.Equal("Source", Part(keys.Parent));
            var groups = Kids(keys);
            Assert.Equal(4, groups.Count);
            foreach (var (at, gate, label, whole, list, choice, reset) in new[]
            {
                (0, "UsesUpDownKeys", "Pad_Row_Up", "ResetLabel_UpInput", "KeyInputsView", "ParamUpInputChoice", "Reset_MappingSourceItem_ParamUpInputChoice_5577"),
                (1, "UsesUpDownKeys", "Pad_Row_Down", "ResetLabel_DownInput", "KeyInputsView", "ParamDownInputChoice", "Reset_MappingSourceItem_ParamDownInputChoice_5618"),
                (2, "IsInvertOnHoldKind", "Pad_Row_Modifier", "ResetLabel_ModifierInput", "ParamInputsView", "ParamModifierInputChoice", "Reset_MappingSourceItem_ParamModifierInputChoice_5659"),
            })
            {
                // The keys are the mode's, kept on the one that holds the mode.
                Assert.Equal("{Binding ModeHolder}", (string)groups[at].Attribute("DataContext"));
                Assert.Equal("{Binding " + gate + Shown, (string)groups[at].Attribute("Visibility"));
                var three = Kids(groups[at]);
                Assert.Equal(new[] { "TextBlock", "ComboBox", "SettingResetButton" }, three.Select(e => e.Name.LocalName));
                // A key's short name. Its reset says the setting's whole name.
                Assert.Equal(("{StaticResource LineKeyLabel}", "{Binding " + label + S), ((string)three[0].Attribute("Style"), (string)three[0].Attribute("Text")));
                Assert.Equal("{Binding " + whole + S, (string)three[2].Attribute("SettingLabel"));
                Assert.Equal("{Binding " + whole + S, (string)three[1].Attribute("AutomationProperties.Name"));
                Assert.Equal("{Binding Row." + list + "}", (string)three[1].Attribute("ItemsSource"));
                Assert.Equal("{Binding " + choice + TwoWay, (string)three[1].Attribute("SelectedItem"));
                Assert.Equal((reset, choice), (Id(three[2]), (string)three[2].Attribute("CommandParameter")));
            }
            var s = Strings.Instance;
            Assert.Equal(new[] { "Up", "Down", "Modifier" }, new[] { s.Pad_Row_Up, s.Pad_Row_Down, s.Pad_Row_Modifier });
            Assert.Equal("{StaticResource LineLabel}", (string)Keyed(Page(), "LineKeyLabel").Attribute("BasedOn"));

            // One Record and one Clear for the keys, the line's own.
            Assert.Null(groups[3].Attribute("DataContext"));
            var buttons = Kids(groups[3]);
            Assert.Equal(new[] { "{Binding LineRecordCommand}", "{Binding LineClearCommand}" }, buttons.Select(b => (string)b.Attribute("Command")));
            Assert.Equal("{StaticResource RecordButton}", (string)buttons[0].Attribute("Style"));
            Assert.Equal("{Binding CanLineRecord}", (string)buttons[0].Attribute("IsEnabled"));
            Assert.Equal(1, keys.Descendants().Count(e => (string)e.Attribute("Command") == "{Binding LineRecordCommand}"));
            Assert.DoesNotContain(keys.Descendants(), e => e.Name.LocalName == "CheckBox");

            // The input's own picker and the three switches go as the keys
            // come, each by a rule of its own that Search can read.
            foreach (string part in new[] { "OwnInput", "InvertPart", "HalfPart", "EitherPart" })
                Assert.Equal(NoKeys, (string)Named(line, part).Attribute("Visibility"));
            Assert.DoesNotContain(TriggersOf(line), t => ((string)t.Attribute("Binding") ?? "").Contains("IsLineKeyMode", StringComparison.Ordinal));
            Assert.True(new MappingSourceItem { Kind = "Ramped" }.IsLineKeyMode);
            Assert.True(new MappingSourceItem { Kind = "InvertOnHold" }.IsLineKeyMode);
            Assert.False(new MappingSourceItem { Kind = "Toggle" }.IsLineKeyMode);

            // Record on such a source asks for its keys in turn, on an
            // input's source for that input, and a second click stops either.
            var source = new MappingSourceItem { Kind = "Incremental" };
            var asked = new List<string>();
            source.StartKeysRecordingRequested += (_, _) => asked.Add("keys");
            source.StartRecordingRequested += (_, _) => asked.Add("input");
            source.StopRecordingRequested += (_, _) => asked.Add("stop");
            source.LineRecordCommand.Execute(null);
            source.Kind = "Direct";
            source.LineRecordCommand.Execute(null);
            source.IsRecording = true;
            source.LineRecordCommand.Execute(null);
            Assert.Equal(new[] { "keys", "input", "stop" }, asked);

            // Clear empties the keys and keeps the source and its mode.
            var row = new MappingItem("RT", "RightTrigger", MappingCategory.Triggers);
            var steps = new MappingSourceItem { Kind = "Incremental" };
            row.ExtraSources.Add(steps);
            steps.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 87", null);
            steps.SetParamKey(MappingSourceItem.ParamRecordTarget.Down, "Button 83", null);
            Assert.Equal(("Button 87", "Button 83"), (steps.ParamUp, steps.ParamDown));
            steps.LineClearCommand.Execute(null);
            Assert.Equal((string.Empty, string.Empty), (steps.ParamUp ?? string.Empty, steps.ParamDown ?? string.Empty));
            Assert.Same(steps, Assert.Single(row.ExtraSources));
            Assert.Equal("Incremental", steps.Kind);
        }

        /// <summary>What a source can be tuned by stands on one line under
        /// it, from Mode's column on: the settings of its mode, the
        /// deadzone past which an axis presses a button, and the dials of
        /// its input. Each group shows where it applies and takes its reset
        /// with it.</summary>
        [Fact]
        public void AModesSettingsAndAnInputsDialsStandOnOneLineUnderTheSource()
        {
            var page = Page();
            var line = Keyed(page, "SourceLine");
            var under = Assert.Single(line.Descendants(), e => Part(e) == "Under");
            Assert.Equal("FlexLinePanel", under.Name.LocalName);
            var three = Kids(under);
            Assert.Equal(new[] { "ContentControl", "StackPanel", "ContentControl" }, three.Select(e => e.Name.LocalName));
            Assert.Equal(("{Binding ModeHolder}", "{StaticResource SourceModeSettings}"), ((string)three[0].Attribute("Content"), (string)three[0].Attribute("ContentTemplate")));
            Assert.Equal(("{Binding}", "{StaticResource SourceDials}"), ((string)three[2].Attribute("Content"), (string)three[2].Attribute("ContentTemplate")));

            // The deadzone: for an input that presses at one, read by a mode that reads the input.
            Assert.Equal("DeadzoneGroup", Name(three[1]));
            Assert.Equal("{Binding ShowsLineDeadZone" + Shown, (string)three[1].Attribute("Visibility"));
            Assert.Contains(three[1].Descendants(), e => e.Name.LocalName == "Slider" && (string)e.Attribute("Value") == "{Binding LineDeadZone" + TwoWay);
            Assert.Contains(three[1].Descendants(), e => (string)e.Attribute("Command") == "{Binding ResetDeadZoneCommand}");
            Assert.DoesNotContain(TriggersOf(line), t => t.Name.LocalName == "MultiDataTrigger");
            var (vm, pad, _, _) = Slot();
            var press = Row(vm, "ButtonA");
            First(press, pad, "DualSense Wireless Controller", "Axis 2");
            var second = Added(press, pad, "DualSense Wireless Controller", "Axis 3");
            Assert.True(press.IsDeadZoneApplicable && press.ShowsLineDeadZone);
            Assert.True(second.IsDeadZoneApplicable && second.ShowsLineDeadZone);
            var said = new List<string>();
            press.PropertyChanged += (_, e) => said.Add(e.PropertyName);
            second.PropertyChanged += (_, e) => said.Add("added " + e.PropertyName);
            // A mode that reads keys leaves the input unread, and its deadzone with it.
            press.PrimaryKindSource.Kind = "Incremental";
            second.Kind = "Incremental";
            Assert.False(press.ShowsLineDeadZone);
            Assert.False(second.ShowsLineDeadZone);
            Assert.Contains(nameof(MappingItem.ShowsLineDeadZone), said);
            Assert.Contains("added " + nameof(MappingSourceItem.ShowsLineDeadZone), said);
            // A button presses by itself and has no deadzone.
            First(Row(vm, "ButtonB"), pad, "DualSense Wireless Controller", "Button 1");
            Assert.False(Row(vm, "ButtonB").ShowsLineDeadZone);

            // The settings of a mode, each group with its gate, its name, what it sets and its reset.
            Assert.Equal(new[]
                {
                    ("IsRapidTriggerKind", "Pad_Mapping_RapidTrigger_Distance", "ParamRapidTriggerDistance", "Reset_MappingSourceItem_ParamRapidTriggerDistance_4316"),
                    ("IsIncrementalKind", "Pad_Mapping_Rate", "ParamRate", "Reset_MappingSourceItem_ParamRate_4305"),
                    ("IsIncrementalKind", "Pad_Mapping_Sticky", "ParamSticky", "Reset_MappingSourceItem_ParamSticky_4308"),
                    ("IsIncrementalKind", "Pad_Mapping_Min", "ParamMin", "Reset_MappingSourceItem_ParamMin_4311"),
                    ("IsIncrementalKind", "Pad_Mapping_Max", "ParamMax", "Reset_MappingSourceItem_ParamMax_4314"),
                    ("IsRampedKind", "Pad_Mapping_Ramp_Attack", "ParamAttackTime", "Reset_MappingSourceItem_ParamAttackTime_4288"),
                    ("IsRampedKind", "Pad_Mapping_Ramp_Release", "ParamReleaseTime", "Reset_MappingSourceItem_ParamReleaseTime_4292"),
                    ("IsRampedKind", "Pad_Mapping_Ramp_Reverse", "ParamReverseMultiplier", "Reset_MappingSourceItem_ParamReverseMultiplier_4296"),
                    ("IsRampedKind", "Pad_Mapping_Ramp_Autocenter", "ParamAutocenter", "Reset_MappingSourceItem_ParamAutocenter_4299"),
                    ("IsKeyInvertShown", "Pad_Invert", "Invert", null),
                },
                Groups(Keyed(page, "SourceModeSettings")));

            // The dials of an input. A reset that is a command of its own is named by the command.
            var dials = Keyed(page, "SourceDials");
            Assert.Equal(new[]
                {
                    ("IsInvertOutputApplicable", "Pad_InvertOutput", "InvertOutput", "Reset_MappingItem_InvertOutput_5776"),
                    ("IsGyroSource", "Mapping_GyroSensitivity", "GyroSensitivity", "ResetGyroSensitivityCommand"),
                    ("IsGyroLeanSource", "Mapping_GyroSensitivity", "Sensitivity", "ResetSensitivityCommand"),
                    ("IsParamAccelApplicable", "Pad_Mapping_Accel", "ParamAccel", "ResetParamAccelCommand"),
                    ("IsMouseCursorSource", "Mapping_MouseCursorSensitivity", "MouseCursorSensitivity", "ResetMouseCursorSensitivityCommand"),
                    ("IsIrPointerSource", "Mapping_IrPointerSensitivity", "IrPointerSensitivity", "ResetIrPointerSensitivityCommand"),
                    ("IsMouseMotionSource", "Mapping_IrPointerSensitivity", "IrPointerSensitivity", "ResetIrPointerSensitivityCommand"),
                },
                Groups(dials));
            // A mode that reads keys leaves the input unread, so it shows no dial.
            Assert.Equal("{Binding IsLineKeyMode, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}",
                (string)Kids(dials).Single().Attribute("Visibility"));
            // Every group but the saved Invert has a reset: every setting keeps its own.
            Assert.All(Groups(Keyed(page, "SourceModeSettings")).Where(g => g.Sets != "Invert").Concat(Groups(dials)), g => Assert.NotNull(g.Reset));
        }

        /// <summary>The groups of a template: each one's gate, its label,
        /// what its first control sets, and its reset (a setting's reset by
        /// its id, a command's by the command).</summary>
        private static List<(string Gate, string Label, string Sets, string Reset)> Groups(XElement template)
        {
            static string KeyOf(string binding)
            {
                if (string.IsNullOrEmpty(binding) || !binding.StartsWith("{Binding ", StringComparison.Ordinal)) return binding;
                int end = binding.IndexOfAny(new[] { ',', '}' }, 9);
                return binding.Substring(9, end - 9);
            }
            var groups = new List<(string, string, string, string)>();
            foreach (var group in template.Descendants().Where(e => e.Name.LocalName == "StackPanel" && (string)e.Attribute("Style") == "{StaticResource LineGroup}"))
            {
                var label = group.Elements().First(e => (string)e.Attribute("Style") == "{StaticResource LineLabel}");
                var control = group.Elements().First(e => e.Name.LocalName is "Slider" or "TextBox" or "CheckBox" or "ComboBox");
                string sets = KeyOf((string)(control.Attribute("Value") ?? control.Attribute("Text") ?? control.Attribute("IsChecked")
                    ?? control.Attribute("SelectedValue") ?? control.Attribute("SelectedItem")));
                var own = group.Elements().FirstOrDefault(e => e.Name.LocalName == "SettingResetButton");
                var command = group.Elements().FirstOrDefault(e => e.Name.LocalName == "Button" && ((string)e.Attribute("Command") ?? "").Contains("Reset", StringComparison.Ordinal));
                groups.Add((KeyOf((string)group.Attribute("Visibility")), KeyOf((string)label.Attribute("Text")), sets,
                    own != null ? Id(own) : command != null ? KeyOf((string)command.Attribute("Command")) : null));
            }
            return groups;
        }

        /// <summary>The output stands last: how the sources combine, Do
        /// Not Inherit, and the live value at the line's end. Then what the
        /// output is tuned by, each where the row has it: the trim's
        /// settings, the motion's, and the formula.</summary>
        [Fact]
        public void TheOutputStandsLast_CombineDoNotInheritAndTheValue()
        {
            var page = Page();
            var zone = Named(Keyed(page, "MappingRowPanel"), "OutputZone");
            var lines = Kids(Assert.Single(Kids(zone)));
            Assert.Equal("FlexLinePanel", lines[0].Name.LocalName);
            Assert.Equal(("True", "44"), ((string)lines[0].Attribute("EndsApart"), (string)lines[0].Attribute("MinLineHeight")));
            var first = Kids(lines[0]);
            Assert.Equal(3, first.Count);

            // Combine: with two sources or more, and with one source whose mode joins two keys.
            Assert.Equal("{Binding IsMultiSource" + Shown, (string)first[0].Attribute("Visibility"));
            var combine = Assert.Single(first[0].Elements(), e => e.Name.LocalName == "ComboBox");
            Assert.Equal("{Binding AvailableCombineModes}", (string)combine.Attribute("ItemsSource"));
            Assert.Equal("{Binding CombineMode" + TwoWay, (string)combine.Attribute("SelectedValue"));
            Assert.Equal("Reset_MappingItem_CombineMode_4340", Id(Assert.Single(first[0].Elements(), e => e.Name.LocalName == "SettingResetButton")));

            // Do Not Inherit: only on a layer that inherits what it leaves unmapped.
            Assert.Equal("{Binding DataContext.IsActiveLayerInheriting, RelativeSource={RelativeSource AncestorType=UserControl}" + Shown,
                (string)first[1].Attribute("Visibility"));
            Assert.Contains(first[1].Elements(), e => e.Name.LocalName == "CheckBox" && (string)e.Attribute("IsChecked") == "{Binding NoInherit" + TwoWay);
            Assert.Equal("Reset_MappingItem_NoInherit_5788", Id(Assert.Single(first[1].Elements(), e => e.Name.LocalName == "SettingResetButton")));

            // The live value ends the line: a bar and the number. A stick's axis rests in the bar's middle.
            Assert.Equal("ValuePart", Name(first[2]));
            Assert.Equal("True", Attached(first[2], "FlexLinePanel.AtEnd"));
            Assert.Equal("{Binding DataContext.ShowRowValue, RelativeSource={RelativeSource AncestorType=UserControl}" + Shown, (string)first[2].Attribute("Visibility"));
            var bar = Assert.Single(first[2].Elements(), e => e.Name.LocalName == "LevelBar");
            Assert.Equal(("{Binding LiveBarLevel, Mode=OneWay}", "{Binding IsCenteredValue, Mode=OneWay}"), ((string)bar.Attribute("Level"), (string)bar.Attribute("IsCentered")));
            var number = Assert.Single(first[2].Descendants(), e => e.Name.LocalName == "Style" && (string)e.Attribute("TargetType") == "TextBlock");
            Assert.Contains(number.Elements(), e => (string)e.Attribute("Property") == "Text" && (string)e.Attribute("Value") == "{Binding CurrentPercentText, Mode=OneWay}");
            var discrete = Assert.Single(number.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding IsTargetDiscrete}", (string)discrete.Attribute("Binding"));
            Assert.Equal("{Binding CurrentValueText, Mode=OneWay}", (string)Assert.Single(discrete.Elements()).Attribute("Value"));

            // Then the trim's settings, the motion's note and settings, and the formula.
            Assert.Equal("{Binding ShouldShowTrimSettings" + Shown, (string)lines[1].Attribute("Visibility"));
            Assert.Equal(new[]
                {
                    ("IsMultiSource", "Pad_Combine_Label", "CombineMode", "Reset_MappingItem_CombineMode_4340"),
                    ("DataContext.IsActiveLayerInheriting", "Pad_Shift_NoInherit", "NoInherit", "Reset_MappingItem_NoInherit_5788"),
                    (null, "Pad_Mapping_TrimDeadzone", "TrimDeadzone", "ResetTrimDeadzoneCommand"),
                    (null, "Pad_Mapping_TrimRate", "TrimRate", "ResetTrimRateCommand"),
                    (null, "Pad_Mapping_TrimReset", "TrimResetOnRelease", "ResetTrimResetOnReleaseCommand"),
                    ("CanUseMotionAngle", "Pad_Mapping_MotionMode", "MotionResponse", "Reset_MappingItem_MotionResponse_4614"),
                    ("ShowMotionSpeedSettings", "Pad_Mapping_MotionTopSpeed", "MotionSpeed", "Reset_MappingItem_MotionSpeed_4624"),
                    ("ShowMotionSpeedSettings", "Pad_Mapping_MotionStartSpeed", "MotionMinSpeed", "Reset_MappingItem_MotionMinSpeed_4630"),
                    ("ShowMotionAngleSettings", "Pad_Mapping_MotionLeanAngle", "MotionAngle", "Reset_MappingItem_MotionAngle_4639"),
                    ("IsMotionAxisRow", "Pad_Mapping_MotionDeadzone", "MotionDeadzone", "Reset_MappingItem_MotionDeadzone_4646"),
                },
                Groups(zone));
            var note = Assert.Single(zone.Descendants(), e => (string)e.Attribute("Text") == "{Binding MotionRowNote}");
            Assert.Equal("{Binding ShowMotionRowNote" + Shown, (string)note.Attribute("Visibility"));
            var formula = Named(zone, "FormulaEditor");
            Assert.Equal("{Binding ShouldShowCustomExpression" + Shown, (string)formula.Attribute("Visibility"));
            Assert.True(lines.IndexOf(formula) > 0, "the formula stands in the output's zone");
            Assert.Equal("Reset_MappingItem_CombineExpression_4415", Id(Assert.Single(formula.Descendants(), e => e.Name.LocalName == "SettingResetButton")));
        }

        // ── the lines' view models ─────────────────────────────────────

        private static string GuidText(Guid id) => id.ToString("D").ToLowerInvariant();

        private static PadViewModel.MappedDeviceInfo Device(Guid id, string name, bool online)
            => new PadViewModel.MappedDeviceInfo { InstanceGuid = id, Name = name, TypeKey = "Gamepad", IsOnline = online };

        /// <summary>A controller with three devices, the third not
        /// connected, and its rows built.</summary>
        private static (PadViewModel Vm, Guid Pad, Guid Keys, Guid Away) Slot()
        {
            var vm = new PadViewModel(0);
            vm.RebuildMappings();
            Guid pad = Guid.NewGuid(), keys = Guid.NewGuid(), away = Guid.NewGuid();
            vm.MappedDevices.Add(Device(pad, "DualSense Wireless Controller", true));
            vm.MappedDevices.Add(Device(keys, "Keyboard", true));
            vm.MappedDevices.Add(Device(away, "Xbox Series X Controller", false));
            return (vm, pad, keys, away);
        }

        private static MappingItem Row(PadViewModel vm, string target) => vm.Mappings.First(m => m.TargetSettingName == target);

        private static void First(MappingItem row, Guid device, string label, string descriptor)
        {
            row.PrimarySourceDeviceGuid = GuidText(device);
            row.PrimarySourceDeviceLabel = label;
            row.LoadDescriptor(descriptor);
        }

        private static MappingSourceItem Added(MappingItem row, Guid device, string label, string descriptor, bool invert = false, string kind = "Direct")
        {
            var source = new MappingSourceItem { Kind = kind, DeviceGuid = GuidText(device), DeviceLabel = label, Descriptor = descriptor, Invert = invert };
            row.ExtraSources.Add(source);
            return source;
        }

        /// <summary>The row's first source and an added one read alike
        /// through the line's interface: the row, the one that holds the
        /// mode, the device and whether it is away, and the three switches,
        /// which are the source's own flags under one name.</summary>
        [Fact]
        public void TheFirstSourceAndAnAddedOneAreTheSameLine()
        {
            var (vm, pad, keys, away) = Slot();
            var row = Row(vm, "ButtonB");
            First(row, pad, "DualSense Wireless Controller", "Button 1");
            var added = Added(row, away, "Xbox Series X Controller", "Button 1");
            IMappingSourceLine first = row, second = added;

            Assert.Same(row, first.Row);
            Assert.Same(row, second.Row);
            Assert.True(first.IsFirstSource);
            Assert.False(second.IsFirstSource);
            Assert.Same(row.PrimaryKindSource, first.ModeHolder);
            Assert.Same(added, second.ModeHolder);
            // The first source's holder is no source of its own, and offers
            // the row's modes: Invert On Hold only while another source is
            // there for it to turn around.
            Assert.Equal(row.PrimaryKindOptions.Select(k => k.Value), row.PrimaryKindSource.ModeChoices.Select(k => k.Value));
            Assert.Contains(row.PrimaryKindSource.ModeChoices, k => k.Value == "InvertOnHold");
            Assert.Same(added.KindChoices, added.ModeChoices);
            var alone = Row(vm, "ButtonX");
            First(alone, pad, "DualSense Wireless Controller", "Button 2");
            Assert.DoesNotContain(alone.PrimaryKindSource.ModeChoices, k => k.Value == "InvertOnHold");
            Assert.Contains(Added(alone, pad, "DualSense Wireless Controller", "Button 3").ModeChoices, k => k.Value == "InvertOnHold");

            Assert.Equal((GuidText(pad), "DualSense Wireless Controller"), (first.LineDeviceGuid, first.LineDeviceLabel));
            Assert.Same(vm.MappedDevices[0], first.LineDevice);
            Assert.False(first.IsLineAway);
            Assert.True(first.CanLineRecord);
            Assert.Same(vm.MappedDevices[2], second.LineDevice);
            Assert.True(second.IsLineAway);
            Assert.False(second.CanLineRecord);
            Assert.False(first.IsLineUnassigned);

            // A device the controller does not have is away too, and no device never is.
            var stranger = Added(row, Guid.NewGuid(), "Gone", "Button 2");
            Assert.Null(stranger.LineDevice);
            Assert.True(stranger.IsLineAway);
            var any = new MappingSourceItem { Kind = "Direct", Descriptor = "Button 3" };
            row.ExtraSources.Add(any);
            Assert.False(any.IsLineAway);
            Assert.True(any.CanLineRecord);
            Assert.True(new MappingSourceItem().IsLineUnassigned);
            Assert.False(PadViewModel.IsAway(null, GuidText(pad)));

            // The switches, on each, with the change said under both names.
            var said = new List<string>();
            row.PropertyChanged += (_, e) => said.Add(e.PropertyName);
            first.LineInvert = true;
            first.LineHalf = true;
            first.LineBidirectional = true;
            first.LineDeadZone = 30;
            Assert.Equal((true, true, true, 30), (row.IsInverted, row.IsHalfAxis, row.IsBidirectional, row.MappingDeadZone));
            foreach (string name in new[] { nameof(MappingItem.LineInvert), nameof(MappingItem.LineHalf), nameof(MappingItem.LineBidirectional), nameof(MappingItem.LineDeadZone) })
                Assert.Contains(name, said);
            said.Clear();
            added.PropertyChanged += (_, e) => said.Add(e.PropertyName);
            second.LineInvert = true;
            second.LineHalf = true;
            second.LineBidirectional = true;
            second.LineDeadZone = 40;
            Assert.Equal((true, true, true, 40), (added.Invert, added.HalfAxis, added.Bidirectional, added.DeadZone));
            foreach (string name in new[] { nameof(MappingSourceItem.LineInvert), nameof(MappingSourceItem.LineHalf), nameof(MappingSourceItem.LineBidirectional), nameof(MappingSourceItem.LineDeadZone) })
                Assert.Contains(name, said);

            // A reset knows the switches by the line's names, on either.
            Assert.True(row.ResetSettingCommand.CanExecute("LineInvert"));
            row.ResetSettingCommand.Execute("LineInvert");
            Assert.False(row.IsInverted);
            added.ResetSettingCommand.Execute("LineHalf");
            Assert.False(added.HalfAxis);
        }

        /// <summary>A button that pushes a stick's axis carries which way,
        /// on the row's first source and on an added one. An axis carries
        /// none, and neither does a row with one direction.</summary>
        [Fact]
        public void AButtonOnAStickAxisSaysWhichWayItPushes()
        {
            var (vm, pad, keys, _) = Slot();
            var stick = Row(vm, "LeftThumbAxisX");
            First(stick, keys, "Keyboard", "Button 68");
            Assert.Equal(MappingSourceItem.DirectionPositive, stick.LineDirection);
            stick.IsInverted = true;
            Assert.Equal(MappingSourceItem.DirectionNegative, stick.LineDirection);
            var left = Added(stick, keys, "Keyboard", "Button 65", invert: true);
            var right = Added(stick, keys, "Keyboard", "Button 70");
            var axis = Added(stick, pad, "DualSense Wireless Controller", "Axis 0");
            Assert.Equal(MappingSourceItem.DirectionNegative, left.LineDirection);
            Assert.Equal(MappingSourceItem.DirectionPositive, right.LineDirection);
            Assert.Equal(string.Empty, axis.LineDirection);
            Assert.Equal(("→ +", "← −"), (MappingSourceItem.DirectionPositive, MappingSourceItem.DirectionNegative));

            // An axis as the first source, and a button's row: no direction.
            First(stick, pad, "DualSense Wireless Controller", "Axis 0");
            Assert.Equal(string.Empty, stick.LineDirection);
            var button = Row(vm, "ButtonA");
            First(button, keys, "Keyboard", "Button 32");
            Assert.Equal(string.Empty, button.LineDirection);
            Assert.Equal(string.Empty, Added(button, keys, "Keyboard", "Button 33").LineDirection);

            // What counts as pressed, as against moved.
            foreach (string pressed in new[] { "Button 5", "POV 0 Up", "Touchpad 0 Click" })
                Assert.True(MappingSourceItem.IsButtonClass(pressed), pressed);
            foreach (string moved in new[] { "Axis 2", "Slider 0", "", null })
                Assert.False(MappingSourceItem.IsButtonClass(moved), moved ?? "null");
        }

        /// <summary>A source has a letter only while the row's formula
        /// shows, the letter the formula knows it by: the sources in their
        /// order, a modifier taking none. The trim stick is the last source
        /// of a row that trims.</summary>
        [Fact]
        public void LettersShowOnlyWithCustom_AndTheTrimStickIsTheLastSource()
        {
            var (vm, pad, _, _) = Slot();
            var row = Row(vm, "ButtonY");
            First(row, pad, "DualSense Wireless Controller", "Button 3");
            var second = Added(row, pad, "DualSense Wireless Controller", "Button 9");
            Assert.False(row.ShouldShowCustomExpression);
            Assert.Equal((string.Empty, string.Empty), (row.LineLetter, second.LineLetter));

            row.CombineMode = "Custom";
            Assert.True(row.ShouldShowCustomExpression);
            Assert.Equal(("a", "b"), (row.LineLetter, second.LineLetter));
            // A modifier turns the others around and has no letter of its own.
            var hold = Added(row, pad, "DualSense Wireless Controller", string.Empty, kind: "InvertOnHold");
            var third = Added(row, pad, "DualSense Wireless Controller", "Button 2");
            Assert.Equal(("a", "b", string.Empty, "c"), (row.LineLetter, second.LineLetter, hold.LineLetter, third.LineLetter));
            // A source taken out, and the rest move up.
            row.ExtraSources.Remove(second);
            Assert.Equal(("a", "b"), (row.LineLetter, third.LineLetter));

            row.CombineMode = "OR";
            Assert.Equal((string.Empty, string.Empty, string.Empty), (row.LineLetter, hold.LineLetter, third.LineLetter));

            // The trim stick.
            var trigger = Row(vm, "LeftTrigger");
            First(trigger, pad, "DualSense Wireless Controller", "Button 9");
            var gate = Added(trigger, pad, "DualSense Wireless Controller", "Button 10");
            var stick = Added(trigger, pad, "DualSense Wireless Controller", "Axis 1");
            Assert.False(stick.IsTrimStick);
            trigger.CombineMode = "StickTrim";
            Assert.True(trigger.ShouldShowTrimSettings);
            Assert.Equal((false, false, true), (trigger.IsTrimStick, gate.IsTrimStick, stick.IsTrimStick));
            trigger.ExtraSources.Remove(stick);
            Assert.True(gate.IsTrimStick);
            trigger.CombineMode = "OR";
            Assert.False(gate.IsTrimStick);
        }

        /// <summary>A stored source becomes a row's first source in one
        /// place, for a load and for Remove on the first source alike. A
        /// source that reads its own input lands in the row's own fields,
        /// its Invert and Half before the input's name. One that reads
        /// keys lands on the one that holds the row's mode.</summary>
        [Fact]
        public void AStoredSourceBecomesTheFirstSourceInOnePlace()
        {
            Assert.Equal("IHAxis 2", MappingItem.WithFlagPrefix("Axis 2", true, true));
            Assert.Equal("HAxis 2", MappingItem.WithFlagPrefix("Axis 2", false, true));
            Assert.Equal("Button 1", MappingItem.WithFlagPrefix("Button 1", false, false));
            Assert.Equal(string.Empty, MappingItem.WithFlagPrefix(string.Empty, true, true));
            Assert.Equal(string.Empty, MappingItem.WithFlagPrefix(null, true, false));

            string device = GuidText(Guid.NewGuid());
            var row = new MappingItem("LT", "LeftTrigger", MappingCategory.Triggers);
            row.LoadFirstSource(new MappingSource
            {
                Kind = "Direct", Descriptor = "Axis 2", DeviceGuid = device, Invert = true, HalfAxis = true, Bidirectional = true,
                DeadZone = 0, InvertOutput = true, ParamAccel = 3, GyroSensitivity = 2.5,
            }, id => id == device ? "Pad" : "?");
            Assert.True(row.IsPrimaryDescriptor);
            Assert.Equal("IHAxis 2", row.SourceDescriptor);
            Assert.Equal((true, true, true), (row.IsInverted, row.IsHalfAxis, row.IsBidirectional));
            // A stored 0 is "use the global threshold", which the editor shows as 50.
            Assert.Equal(50, row.MappingDeadZone);
            Assert.True(row.InvertOutput);
            Assert.Equal(3, row.ParamAccel);
            Assert.Equal(2.5, row.GyroSensitivity);
            // A sensitivity that was never stored is 1.
            Assert.Equal((1.0, 1.0, 1.0), (row.MouseCursorSensitivity, row.IrPointerSensitivity, row.Sensitivity));
            Assert.Equal((device, "Pad"), (row.PrimarySourceDeviceGuid, row.PrimarySourceDeviceLabel));
            Assert.Equal("Direct", row.PrimaryKindSource?.Kind ?? "Direct");

            // A source that reads keys: the row's own fields are empty, and the mode's holder has it.
            row.LoadFirstSource(new MappingSource { Kind = "Incremental", DeviceGuid = device, ParamUp = "Button 5", ParamUpDeviceGuid = device }, _ => "Pad");
            Assert.False(row.IsPrimaryDescriptor);
            Assert.True(row.IsLineKeyMode);
            Assert.Equal(string.Empty, row.SourceDescriptor ?? string.Empty);
            Assert.Equal(string.Empty, row.PrimarySourceDeviceGuid);
            Assert.Equal((50, false, false, 0.0, 1.0), (row.MappingDeadZone, row.IsBidirectional, row.InvertOutput, row.ParamAccel, row.GyroSensitivity));
            Assert.Equal(("Incremental", "Button 5"), (row.PrimaryKindSource.Kind, row.PrimaryKindSource.ParamUp));

            // No source: the first source is empty.
            row.LoadFirstSource(null, _ => "Pad");
            Assert.True(row.IsPrimaryDescriptor);
            Assert.Equal(string.Empty, row.SourceDescriptor ?? string.Empty);
            Assert.Equal("Direct", row.PrimaryKindSource?.Kind ?? "Direct");
        }

        /// <summary>Remove on the first source's line: the next source
        /// becomes the first, exactly as it would after a save and a load,
        /// and with no next source the row is cleared.</summary>
        [Fact]
        public void RemoveOnTheFirstSourceMovesTheNextOneUp()
        {
            string pad = GuidText(Guid.NewGuid()), keys = GuidText(Guid.NewGuid());
            var row = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            row.PrimarySourceDeviceGuid = pad;
            row.PrimarySourceDeviceLabel = "Pad";
            row.LoadDescriptor("Button 0");
            var next = new MappingSourceItem { Kind = "Direct", DeviceGuid = keys, DeviceLabel = "Keyboard", Descriptor = "Button 32", Invert = true, DeadZone = 30 };
            var last = new MappingSourceItem { Kind = "Direct", DeviceGuid = pad, DeviceLabel = "Pad", Descriptor = "Button 1" };
            row.ExtraSources.Add(next);
            row.ExtraSources.Add(last);
            Assert.Same(row.RemoveFirstSourceCommand, row.LineRemoveCommand);

            row.LineRemoveCommand.Execute(null);
            Assert.Same(last, Assert.Single(row.ExtraSources));
            Assert.Equal("IButton 32", row.SourceDescriptor);
            Assert.True(row.IsInverted);
            Assert.Equal(30, row.MappingDeadZone);
            Assert.Equal((keys, "Keyboard"), (row.PrimarySourceDeviceGuid, row.PrimarySourceDeviceLabel));

            // A source that reads keys moves up onto the mode's holder.
            last.Kind = "Incremental";
            last.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 87", keys);
            row.LineRemoveCommand.Execute(null);
            Assert.Empty(row.ExtraSources);
            Assert.False(row.IsPrimaryDescriptor);
            Assert.Equal(("Incremental", "Button 87"), (row.PrimaryKindSource.Kind, row.PrimaryKindSource.ParamUp));

            // The last source: the row is cleared.
            row.LineRemoveCommand.Execute(null);
            Assert.Empty(row.ExtraSources);
            Assert.True(row.IsPrimaryDescriptor);
            Assert.Equal(string.Empty, row.SourceDescriptor ?? string.Empty);

            // An added source's Remove takes that source off the row.
            var extra = new MappingSourceItem { Kind = "Direct", Descriptor = "Button 4" };
            row.ExtraSources.Add(extra);
            extra.LineRemoveCommand.Execute(null);
            Assert.Empty(row.ExtraSources);
        }

        // ── the list ───────────────────────────────────────────────────

        /// <summary>The list's head and a row's line are one grid: what
        /// games see in the first column, then a column for each device
        /// that drives the controller, then the row's end. The cells of the
        /// head and of every row stand in the one kind of panel, at the
        /// columns' own widths, and scroll sideways together.</summary>
        [Fact]
        public void TheHeadAndEveryRowAreOneGridOfDeviceColumns()
        {
            var page = Page();
            var head = Named(page, "ListHead");
            Assert.Equal("2", (string)head.Attribute("Grid.Row"));
            // It shows with the table: in the list, while the list has rows.
            var hides = head.Descendants().Where(e => e.Name.LocalName == "DataTrigger").ToList();
            Assert.Equal(new[] { ("{Binding IsButtonsPictureView}", "True"), ("{Binding HasItems, ElementName=MappingDataGrid}", "False") },
                hides.Select(t => ((string)t.Attribute("Binding"), (string)t.Attribute("Value"))));
            Assert.All(hides, t => Assert.Equal("Collapsed", (string)Assert.Single(t.Elements()).Attribute("Value")));

            var line = Named(head, "ListHeadLine");
            var row = Named(page, "RowLine");
            var foot = Named(page, "ListCellsScroll");
            // The same three columns in the head, in a row and under the table.
            foreach (var grid in new[] { line, row, foot })
            {
                var columns = grid.Elements().First(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements().ToList();
                Assert.Equal(new[] { "Auto", "*", "Auto" }, columns.Select(c => (string)c.Attribute("Width")));
                Assert.Equal(("212", "CompactRowOutput", "ListRowEnd"),
                    ((string)columns[0].Attribute("MinWidth"), (string)columns[0].Attribute("SharedSizeGroup"), (string)columns[2].Attribute("SharedSizeGroup")));
            }
            Assert.Equal((string)row.Attribute("Margin"), (string)line.Attribute("Margin"));
            // The widths are shared across the head and the table.
            Assert.Equal("True", (string)Named(page, "ButtonsPage").Attribute("Grid.IsSharedSizeScope"));
            Assert.Null(Named(page, "MappingDataGrid").Attribute("Grid.IsSharedSizeScope"));

            Assert.Contains(line.Elements(), e => (string)e.Attribute("Text") == "{Binding Pad_List_GamesSee" + S);
            Assert.Equal("Games see", Strings.Instance.Pad_List_GamesSee);
            var headCells = Named(line, "ListHeadCells");
            Assert.Equal(("{Binding ListColumns}", "{StaticResource ListColumnHead}", "{StaticResource ListHeadContainer}", "{StaticResource ListCellsPanelTemplate}"),
                ((string)headCells.Attribute("ItemsSource"), (string)headCells.Attribute("ItemTemplate"), (string)headCells.Attribute("ItemContainerStyle"), (string)headCells.Attribute("ItemsPanel")));
            // The head tells the list how much room the cells have.
            Assert.Equal("ListHeadCells_SizeChanged", (string)headCells.Attribute("SizeChanged"));
            var rowCells = Named(row, "RowCells");
            Assert.Equal(("{Binding Cells}", "{StaticResource ListCell}", "{StaticResource ListCellContainer}", "{StaticResource ListCellsPanelTemplate}"),
                ((string)rowCells.Attribute("ItemsSource"), (string)rowCells.Attribute("ItemTemplate"), (string)rowCells.Attribute("ItemContainerStyle"), (string)rowCells.Attribute("ItemsPanel")));
            Assert.Equal("1", (string)rowCells.Attribute("Grid.Column"));

            // The panel: the columns' least width is the list's own number, and the offset the page's.
            var panel = Assert.Single(Keyed(page, "ListCellsPanelTemplate").Elements());
            Assert.Equal("ListCellsPanel", panel.Name.LocalName);
            Assert.Equal("{x:Static vm:ListColumn.MinWidth}", (string)panel.Attribute("MinColumnWidth"));
            Assert.Equal("{Binding DataContext.ListCellsOffset, RelativeSource={RelativeSource AncestorType=UserControl}}", (string)panel.Attribute("Offset"));
            Assert.Equal((180.0, 150.0), (ListColumn.MinWidth, ListColumn.FoldWidth));
            // A column that is folded away is not drawn, in the head and in a row.
            foreach (var (key, shown, width) in new[] { ("ListCellContainer", "{Binding Column.IsShown" + Shown, "{Binding Column.FixedWidth}"), ("ListHeadContainer", "{Binding IsShown" + Shown, "{Binding FixedWidth}") })
            {
                var container = Keyed(page, key);
                Assert.Contains(container.Elements(), e => (string)e.Attribute("Property") == "Visibility" && (string)e.Attribute("Value") == shown);
                Assert.Contains(container.Elements(), e => (string)e.Attribute("Property") == "controls:ListCellsPanel.FixedWidth" && (string)e.Attribute("Value") == width);
            }

            // The sideways scroll: one bar at the table's foot, under the cells.
            Assert.Equal("3", (string)foot.Attribute("Grid.Row"));
            var shows = Assert.Single(foot.Descendants(), e => e.Name.LocalName == "MultiDataTrigger");
            Assert.Equal(new[] { ("{Binding ShowListCellsScroll}", "True"), ("{Binding IsButtonsListView}", "True") },
                shows.Descendants().Where(e => e.Name.LocalName == "Condition").Select(c => ((string)c.Attribute("Binding"), (string)c.Attribute("Value"))));
            var bar = Assert.Single(foot.Descendants(), e => e.Name.LocalName == "ScrollBar");
            Assert.Equal("Horizontal", (string)bar.Attribute("Orientation"));
            Assert.Equal("1", (string)bar.Attribute("Grid.Column"));
            Assert.Equal("{Binding ListCellsScrollMax, Mode=OneWay}", (string)bar.Attribute("Maximum"));
            Assert.Equal("{Binding ListCellsViewport, Mode=OneWay}", (string)bar.Attribute("ViewportSize"));
            Assert.StartsWith("{Binding ListCellsOffset, Mode=TwoWay", (string)bar.Attribute("Value"));
            Assert.Equal("{Binding Pad_List_Scroll" + S, (string)bar.Attribute("AutomationProperties.Name"));
        }

        /// <summary>A column's head: the device's light, its name, and
        /// under it its kind or that it is not connected. The fold's head
        /// says how many devices it stands for, and a click on it gives
        /// each its column back.</summary>
        [Fact]
        public void AColumnsHeadNamesItsDevice_AndTheFoldsHeadOpensIt()
        {
            var head = Keyed(Page(), "ListColumnHead");
            var device = Named(head, "DeviceHead");
            Assert.Equal("LeftToRight", (string)Named(device, "Light").Attribute("FlowDirection"));
            Assert.Equal("{Binding Name}", (string)Named(device, "Name").Attribute("Text"));
            Assert.Contains(device.Descendants(), e => (string)e.Attribute("Text") == "{Binding TypeGlyph}");
            Assert.Contains(device.Descendants(), e => (string)e.Attribute("Text") == "{Binding TypeName}");
            Assert.Contains(device.Descendants(), e => (string)e.Attribute("Visibility") == "{Binding ShowsType" + Shown);
            var away = Named(device, "Away");
            Assert.Equal(("{Binding Home_Device_NotConnected" + S, "Collapsed"), ((string)away.Attribute("Text"), (string)away.Attribute("Visibility")));

            var fold = Named(head, "FoldHead");
            Assert.Equal("Button", fold.Name.LocalName);
            Assert.Equal("Collapsed", (string)fold.Attribute("Visibility"));
            Assert.Equal("{Binding DataContext.OpenAwayColumnsCommand, RelativeSource={RelativeSource AncestorType=UserControl}}", (string)fold.Attribute("Command"));
            Assert.Equal("{Binding Pad_List_ShowNotConnected_Tooltip" + S, (string)fold.Attribute("ToolTip"));
            Assert.StartsWith("{Binding Name,", (string)fold.Attribute("AutomationProperties.Name"));
            Assert.Equal("LeftToRight", (string)Assert.Single(fold.Descendants(), e => e.Name.LocalName == "Path").Attribute("FlowDirection"));

            var triggers = TriggersOf(head);
            var folded = Assert.Single(triggers, t => (string)t.Attribute("Binding") == "{Binding IsFold}");
            Assert.Contains(("DeviceHead", "Visibility", "Collapsed"), Setters(folded));
            Assert.Contains(("FoldHead", "Visibility", "Visible"), Setters(folded));
            var gone = Assert.Single(triggers, t => (string)t.Attribute("Binding") == "{Binding IsAway}");
            Assert.Contains(("Away", "Visibility", "Visible"), Setters(gone));
            Assert.Single(triggers, t => (string)t.Attribute("Binding") == "{Binding Device.IsSending}");
        }

        /// <summary>A cell holds a tag for each input of its column's
        /// device that drives the row: the input's name, a key's word
        /// before it, a direction after it, an axis's live level beside it,
        /// and the source's mode after the source's last tag when the mode
        /// is not Direct. A click on a cell records. The fold's cell counts
        /// the inputs on the devices it stands for.</summary>
        [Fact]
        public void ACellIsATagForEachInput_AndAClickOnItRecords()
        {
            var page = Page();
            var tag = Keyed(page, "ListTag");
            var texts = tag.Descendants().Where(e => e.Name.LocalName == "TextBlock").ToList();
            Assert.Equal(new[] { "{Binding Word}", "{Binding Text}", "{Binding Direction}", "{Binding LevelText, Mode=OneWay}", "{Binding Mode}" },
                texts.Select(t => (string)t.Attribute("Text")));
            foreach (int at in new[] { 0, 2, 4 })
                Assert.Equal(((string)texts[at].Attribute("Text")).TrimEnd('}') + ", Converter={StaticResource StringToVisibility}}", (string)texts[at].Attribute("Visibility"));
            // A direction's arrow never mirrors.
            Assert.Equal("LeftToRight", (string)texts[2].Attribute("FlowDirection"));
            var level = Assert.Single(tag.Descendants(), e => e.Name.LocalName == "LevelBar");
            Assert.Equal(("{Binding Level, Mode=OneWay}", "{Binding IsCentered, Mode=OneWay}"), ((string)level.Attribute("Level"), (string)level.Attribute("IsCentered")));
            // The bar and the number show together, and each is a part of the
            // tag's line: in a column short of room the bar gives way and the
            // name trims, and the number is never cut (ListTagPanel).
            Assert.Equal("{Binding ShowsLevel" + Shown, (string)level.Attribute("Visibility"));
            Assert.Equal("{Binding ShowsLevel" + Shown, (string)texts[3].Attribute("Visibility"));
            Assert.Equal("ListTagPanel", level.Parent.Name.LocalName);
            Assert.Same(level.Parent, texts[3].Parent);
            static string PartOf(XElement e) => e.Attributes().SingleOrDefault(a => a.Name.LocalName == "ListTagPanel.Part")?.Value;
            Assert.Equal("Bar", PartOf(level));
            Assert.Null(level.Attribute("Width"));
            var name = Assert.Single(level.Parent.Elements(), e => PartOf(e) == "Name");
            Assert.Contains(texts[1], name.Descendants());
            Assert.Equal("CharacterEllipsis", (string)texts[1].Attribute("TextTrimming"));
            Assert.Null(PartOf(texts[3]));

            var cell = Keyed(page, "ListCell");
            var box = Named(cell, "Cell");
            // Every column stands behind a hairline at its left, connected or
            // not: no trigger gives it to some columns alone.
            Assert.Equal(("1,0,0,0", "14,0,6,0"), ((string)box.Attribute("BorderThickness"), (string)box.Attribute("Padding")));
            Assert.DoesNotContain(TriggersOf(cell).SelectMany(Setters), s => s.Target == "Cell" && s.Property is "BorderThickness" or "Padding");
            var headBox = Named(Keyed(page, "ListColumnHead"), "Head");
            Assert.Equal(("1,0,0,0", "14,0,10,0"), ((string)headBox.Attribute("BorderThickness"), (string)headBox.Attribute("Padding")));
            Assert.DoesNotContain(TriggersOf(Keyed(page, "ListColumnHead")).SelectMany(Setters), s => s.Target == "Head" && s.Property is "BorderThickness" or "Padding");
            Assert.Equal(("ListCell_MouseLeftButtonDown", "ListCell_MouseLeftButtonUp", "Hand", "Transparent"),
                ((string)box.Attribute("MouseLeftButtonDown"), (string)box.Attribute("MouseLeftButtonUp"), (string)box.Attribute("Cursor"), (string)box.Attribute("Background")));
            var tip = Assert.Single(box.Descendants(), e => e.Name.LocalName == "MultiBinding");
            Assert.Equal(new[] { "Row.TargetLabel", "Pad_List_Cell_Tooltip" }, tip.Elements().Select(b => (string)b.Attribute("Path")));
            Assert.Contains("{0}", Strings.Instance.Pad_List_Cell_Tooltip);
            var tags = Named(cell, "Tags");
            Assert.Equal(("{Binding Tags}", "{StaticResource ListTag}"), ((string)tags.Attribute("ItemsSource"), (string)tags.Attribute("ItemTemplate")));
            Assert.Equal("{Binding Count}", (string)Assert.Single(Named(cell, "Count").Descendants(), e => e.Name.LocalName == "TextBlock").Attribute("Text"));
            // A device that is not connected keeps its tags, dimmed, and its cell and the fold's take no click.
            var triggers = TriggersOf(cell);
            foreach (string binding in new[] { "{Binding Column.IsAway}", "{Binding Column.IsFold}" })
            {
                var quiet = Assert.Single(triggers, t => (string)t.Attribute("Binding") == binding);
                Assert.Contains(("Cell", "Cursor", "Arrow"), Setters(quiet));
                Assert.Contains(("Cell", "ToolTip", "{x:Null}"), Setters(quiet));
            }
            Assert.Contains(("Tags", "Opacity", "0.45"), Setters(triggers.First(t => (string)t.Attribute("Binding") == "{Binding Column.IsAway}")));
            Assert.Contains(("Count", "Visibility", "Visible"), Setters(Assert.Single(triggers, t => (string)t.Attribute("Binding") == "{Binding HasCount}")));

            // Which cells record: one of a connected device's column, on a row that can record.
            var (vm, pad, _, away) = Slot();
            var row = Row(vm, "ButtonB");
            First(row, pad, "DualSense Wireless Controller", "Button 1");
            Added(row, away, "Xbox Series X Controller", "Button 1");
            var cells = row.Cells;
            Assert.Equal(new[] { ListColumnKind.Device, ListColumnKind.Device, ListColumnKind.Device, ListColumnKind.Fold }, cells.Select(c => c.Column.Kind));
            Assert.True(PadPage.CellRecords(cells[0]));
            // An empty cell of a connected device records a new source from it.
            Assert.Empty(cells[1].Tags);
            Assert.True(PadPage.CellRecords(cells[1]));
            Assert.False(PadPage.CellRecords(cells[2]));
            Assert.False(PadPage.CellRecords(cells[3]));
            Assert.False(PadPage.CellRecords(null));

            // A click on a cell leaves the row closed, and records from that cell's device alone.
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Contains("if (IsInsideListCell(e.OriginalSource as DependencyObject, row)) return;", code);
            Assert.Contains("ListCellRecordRequested?.Invoke(this, new ListCellRecordArgs(cell.Row, tag, device));", code);
            Assert.Contains("internal Guid ListenOnly", ExtrasPageTests.RepoText("PadForge.App", "Services", "RecorderService.cs"));
        }

        /// <summary>The columns: sources that name no device first, then a
        /// column for each of the controller's devices in the Driven By
        /// line's order, then one for each device a source names that the
        /// controller no longer has, and the fold last. A row's tags stand
        /// under the columns of the devices they are read on.</summary>
        [Fact]
        public void TheColumnsAreTheDevices_AndARowsTagsStandUnderThem()
        {
            var (vm, pad, keys, away) = Slot();
            var b = Row(vm, "ButtonB");
            First(b, pad, "DualSense Wireless Controller", "Button 1");
            Added(b, keys, "Keyboard", "Button 32");
            Added(b, away, "Xbox Series X Controller", "Button 1");
            vm.FlushList();
            Assert.Equal(new[] { ListColumnKind.Device, ListColumnKind.Device, ListColumnKind.Device, ListColumnKind.Fold }, vm.ListColumns.Select(c => c.Kind));
            Assert.Equal(new[] { "DualSense Wireless Controller", "Keyboard", "Xbox Series X Controller" }, vm.ListColumns.Take(3).Select(c => c.Name));
            Assert.Equal(new[] { false, false, true }, vm.ListColumns.Take(3).Select(c => c.IsAway));
            Assert.Equal(new[] { GuidText(pad), GuidText(keys), GuidText(away) }, vm.ListColumns.Take(3).Select(c => c.Guid));
            Assert.Same(vm.MappedDevices[1], vm.ListColumns[1].Device);
            // With room for every column the fold holds none and does not show.
            Assert.Equal(new[] { true, true, true, false }, vm.ListColumns.Select(c => c.IsShown));
            Assert.True(vm.ListColumns[3].IsFold);

            Assert.Equal(new[] { new[] { "Button 1" }, new[] { "Button 32" }, new[] { "Button 1" }, Array.Empty<string>() },
                b.Cells.Select(c => c.Tags.Select(t => t.Text).ToArray()));
            Assert.All(b.Cells, c => Assert.Same(b, c.Row));
            // A row nothing drives has an empty cell under every column.
            Assert.All(Row(vm, "ButtonX").Cells, c => Assert.Empty(c.Tags));
            Assert.Equal(4, Row(vm, "ButtonX").Cells.Count);

            // A source with no device: a column of its own, first.
            var any = new MappingSourceItem { Kind = "Direct", Descriptor = "Button 7" };
            b.ExtraSources.Add(any);
            // A source on a device the controller does not have: a column after the devices, away.
            var gone = Guid.NewGuid();
            Added(b, gone, "Old Wheel", "Button 2");
            vm.FlushList();
            Assert.Equal(new[] { ListColumnKind.AnyDevice, ListColumnKind.Device, ListColumnKind.Device, ListColumnKind.Device, ListColumnKind.Gone, ListColumnKind.Fold },
                vm.ListColumns.Select(c => c.Kind));
            Assert.Equal(Strings.Instance.Mapping_AnyDevice, vm.ListColumns[0].Name);
            Assert.Equal(("Old Wheel", true, GuidText(gone)), (vm.ListColumns[4].Name, vm.ListColumns[4].IsAway, vm.ListColumns[4].Guid));
            Assert.Equal(new[] { "Button 7" }, b.Cells[0].Tags.Select(t => t.Text));
            Assert.Equal(new[] { "Button 2" }, b.Cells[4].Tags.Select(t => t.Text));

            // A device that connects is away no longer, and the same columns stand.
            var before = vm.ListColumns.ToList();
            vm.MappedDevices[2].IsOnline = true;
            vm.FlushList();
            Assert.Equal(before, vm.ListColumns);
            Assert.False(vm.ListColumns[3].IsAway);
            // A device taken off the controller: its source's column is a gone one.
            vm.MappedDevices.RemoveAt(1);
            vm.FlushList();
            Assert.Equal(new[] { ListColumnKind.AnyDevice, ListColumnKind.Device, ListColumnKind.Device, ListColumnKind.Gone, ListColumnKind.Gone, ListColumnKind.Fold },
                vm.ListColumns.Select(c => c.Kind));
            Assert.Contains(vm.ListColumns, c => c.Kind == ListColumnKind.Gone && c.Name == "Keyboard");
        }

        /// <summary>A tag: a source read through its own input is one, with
        /// its direction on a stick's axis and its mode after it when the
        /// mode is not Direct. A source whose mode reads keys is a tag for
        /// each key that is set, with the key's word, and the mode after
        /// the last. An axis of a connected device shows its level.</summary>
        [Fact]
        public void ATagNamesAnInput_ItsKeysWordItsDirectionAndItsMode()
        {
            var (vm, pad, keys, away) = Slot();
            var s = Strings.Instance;

            // The mode, when it is not Direct.
            var x = Row(vm, "ButtonX");
            First(x, pad, "DualSense Wireless Controller", "Button 2");
            Assert.Equal(string.Empty, Assert.Single(x.Cells[0].Tags).Mode);
            x.PrimaryKindSource.Kind = "Toggle";
            Assert.Equal(x.PrimaryKindLabel, Assert.Single(x.Cells[0].Tags).Mode);
            Assert.NotEqual(string.Empty, x.PrimaryKindLabel);

            // A mode's keys, each with its word, and the mode once.
            var trigger = Row(vm, "RightTrigger");
            First(trigger, pad, "DualSense Wireless Controller", "Axis 5");
            var steps = Added(trigger, keys, "Keyboard", string.Empty, kind: "Incremental");
            steps.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 87", GuidText(keys));
            steps.SetParamKey(MappingSourceItem.ParamRecordTarget.Down, "Button 83", GuidText(keys));
            var onKeys = trigger.Cells[1].Tags;
            Assert.Equal(new[] { (s.Pad_Row_Up, "Button 87"), (s.Pad_Row_Down, "Button 83") }, onKeys.Select(t => (t.Word, t.Text)));
            Assert.Equal(new[] { string.Empty, steps.KindLabel }, onKeys.Select(t => t.Mode));
            Assert.Equal(new MappingSourceItem.ParamRecordTarget?[] { MappingSourceItem.ParamRecordTarget.Up, MappingSourceItem.ParamRecordTarget.Down }, onKeys.Select(t => t.Key));
            Assert.All(onKeys, t => Assert.Same(steps, t.Line));
            // A key that is not set has no tag.
            steps.SetParamKey(MappingSourceItem.ParamRecordTarget.Down, string.Empty, null);
            Assert.Equal(s.Pad_Row_Up, Assert.Single(trigger.Cells[1].Tags).Word);
            // The modifier's word.
            var hold = Added(trigger, pad, "DualSense Wireless Controller", string.Empty, kind: "InvertOnHold");
            hold.SetParamKey(MappingSourceItem.ParamRecordTarget.Modifier, "Button 9", GuidText(pad));
            Assert.Contains(trigger.Cells[0].Tags, t => t.Word == s.Pad_Row_Modifier && t.Text == "Button 9" && t.Mode == hold.KindLabel);

            // An axis of a connected device shows its level, and of one that is away none.
            var axis = trigger.Cells[0].Tags[0];
            Assert.Equal(("Axis 5", true, true), (axis.Text, axis.HasLevel, axis.ShowsLevel));
            Assert.Contains(axis, trigger.LiveTags);
            Added(trigger, away, "Xbox Series X Controller", "Axis 5");
            var asleep = Assert.Single(trigger.Cells[2].Tags);
            Assert.Equal((true, false), (asleep.HasLevel, asleep.ShowsLevel));
            Assert.DoesNotContain(asleep, trigger.LiveTags);
            // A button has no level.
            Assert.False(Assert.Single(x.Cells[0].Tags).HasLevel);

            // A direction, on a stick's axis.
            var stick = Row(vm, "LeftThumbAxisX");
            First(stick, pad, "DualSense Wireless Controller", "Axis 0");
            Added(stick, keys, "Keyboard", "Button 68");
            Added(stick, keys, "Keyboard", "Button 65", invert: true);
            Assert.Equal(string.Empty, Assert.Single(stick.Cells[0].Tags).Direction);
            Assert.Equal(new[] { MappingSourceItem.DirectionPositive, MappingSourceItem.DirectionNegative }, stick.Cells[1].Tags.Select(t => t.Direction));

            // The closed row names how its sources combine, where the open row offers the choice.
            Assert.True(stick.ShowsCombine == (stick.IsMultiSource && !string.IsNullOrEmpty(stick.CombineMode)));
            Assert.False(Row(vm, "ButtonA").ShowsCombine);

            // The percent a level reads: a share of the range, or the distance from the middle.
            Assert.Equal(new[] { 62, 0, 100 }, new[] { InputService.ListTagPercent(0.62, false), InputService.ListTagPercent(0, false), InputService.ListTagPercent(1, false) });
            Assert.Equal(new[] { -18, 0, 100, -100 }, new[] { 0.41, 0.5, 1, 0 }.Select(v => InputService.ListTagPercent(v, true)));
        }

        /// <summary>While the list has no room for every column at its
        /// least width, the devices that are not connected fold into one
        /// column, which counts their inputs in each row. A click on its
        /// head opens them. If the columns still do not fit, the cells
        /// scroll sideways.</summary>
        [Fact]
        public void TheAwayDevicesFoldWhenTheColumnsDoNotFit_AndTheCellsScrollPastThat()
        {
            Assert.False(PadViewModel.ColumnsAreTight(3, 540));
            Assert.True(PadViewModel.ColumnsAreTight(3, 539));
            Assert.False(PadViewModel.ColumnsAreTight(0, 10));
            // No width yet: nothing folds before the page has been laid out.
            Assert.False(PadViewModel.ColumnsAreTight(3, 0));

            var (vm, pad, _, away) = Slot();
            var b = Row(vm, "ButtonB");
            First(b, pad, "DualSense Wireless Controller", "Button 1");
            Added(b, away, "Xbox Series X Controller", "Button 1");
            Added(b, away, "Xbox Series X Controller", "Button 2");
            vm.FlushList();
            var said = new List<string>();
            vm.PropertyChanged += (_, e) => said.Add(e.PropertyName);

            // Room for the three: nothing folds, nothing scrolls.
            vm.SetListCellsWidth(600);
            Assert.False(vm.IsAwayFolded);
            Assert.False(vm.ShowListCellsScroll);
            Assert.Equal(600, vm.ListCellsViewport);

            // Too narrow for three: the one that is away folds, and two and the fold fit.
            vm.SetListCellsWidth(520);
            Assert.True(vm.IsAwayFolded);
            Assert.Contains(nameof(PadViewModel.IsAwayFolded), said);
            Assert.Equal(new[] { true, true, false, true }, vm.ListColumns.Select(c => c.IsShown));
            Assert.Equal(string.Format(Strings.Instance.Pad_List_NotConnected_Format, 1), vm.ListColumns[3].Name);
            Assert.False(vm.ShowListCellsScroll);
            // The fold's cell counts the row's inputs on the devices it stands for.
            Assert.Equal((2, true), (b.Cells[3].Count, b.Cells[3].HasCount));
            Assert.False(Row(vm, "ButtonA").Cells[3].HasCount);

            // Narrower than two columns and the fold: the cells scroll by what is missing.
            said.Clear();
            vm.SetListCellsWidth(500);
            Assert.True(vm.ShowListCellsScroll);
            Assert.Equal(2 * ListColumn.MinWidth + ListColumn.FoldWidth - 500, vm.ListCellsScrollMax);
            Assert.Contains(nameof(PadViewModel.ShowListCellsScroll), said);
            vm.ListCellsOffset = 400;
            Assert.Equal(vm.ListCellsScrollMax, vm.ListCellsOffset);
            vm.ListCellsOffset = -5;
            Assert.Equal(0, vm.ListCellsOffset);

            // A click on the fold's head: each device has its column again, and the cells scroll.
            vm.SetListCellsWidth(520);
            vm.OpenAwayColumnsCommand.Execute(null);
            Assert.False(vm.IsAwayFolded);
            Assert.Equal(new[] { true, true, true, false }, vm.ListColumns.Select(c => c.IsShown));
            Assert.Equal(3 * ListColumn.MinWidth - 520, vm.ListCellsScrollMax);
            // It holds while the list stays as narrow, and is forgotten once there is room.
            vm.SetListCellsWidth(510);
            Assert.False(vm.IsAwayFolded);
            vm.SetListCellsWidth(600);
            vm.SetListCellsWidth(520);
            Assert.True(vm.IsAwayFolded);
            // With every device connected there is nothing to fold: the cells scroll.
            vm.MappedDevices[2].IsOnline = true;
            vm.FlushList();
            Assert.False(vm.IsAwayFolded);
            Assert.True(vm.ShowListCellsScroll);
            // A scroll that was further than the new end comes back to it.
            vm.ListCellsOffset = vm.ListCellsScrollMax;
            vm.SetListCellsWidth(535);
            Assert.Equal(5, vm.ListCellsScrollMax);
            Assert.Equal(5, vm.ListCellsOffset);
        }

        /// <summary>What a screen reader says for the list's parts. A
        /// column's head, a cell and a tag are items of plain lists, and
        /// an item is read by its ToString(). A cell says its device: the
        /// inputs are the items inside it. Without these an empty cell
        /// read its class's name, a cell with an input read the input
        /// with no device, and a key's tag read the word "Up" with no key
        /// (seen 2026-11-15 in the running program's automation tree).</summary>
        [Fact]
        public void TheListsPartsSayTheirNamesToAScreenReader()
        {
            var (vm, pad, keys, _) = Slot();
            var trigger = Row(vm, "RightTrigger");
            First(trigger, pad, "DualSense Wireless Controller", "Axis 5");
            var steps = Added(trigger, keys, "Keyboard", string.Empty, kind: "Incremental");
            steps.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 87", GuidText(keys));
            var stick = Row(vm, "LeftThumbAxisX");
            First(stick, keys, "Keyboard", "Button 68");
            vm.FlushList();
            string[] devices = { "DualSense Wireless Controller", "Keyboard", "Xbox Series X Controller" };

            // A column, by its name.
            Assert.Equal(devices, vm.ListColumns.Take(3).Select(c => c.ToString()));
            // A cell, by its column's device, with inputs in it or with none.
            Assert.Equal(devices, trigger.Cells.Take(3).Select(c => c.ToString()));
            Assert.Empty(trigger.Cells[2].Tags);
            // The fold's cell, by what the fold stands for.
            vm.SetListCellsWidth(520);
            Assert.Equal(string.Format(Strings.Instance.Pad_List_NotConnected_Format, 1), trigger.Cells[3].ToString());

            // A tag, by all it shows but its live level: the key's word,
            // the input, which way it pushes, and the mode.
            Assert.Equal("Axis 5", trigger.Cells[0].Tags[0].ToString());
            Assert.Equal(Strings.Instance.Pad_Row_Up + " Button 87 " + steps.KindLabel, trigger.Cells[1].Tags[0].ToString());
            Assert.Equal("Button 68 " + MappingSourceItem.DirectionPositive, stick.Cells[1].Tags[0].ToString());

            // None reads as its class's name. That is what the program's
            // naming pass looks for before it names an item by the first
            // text it shows (AccessibleNames.NameItem).
            var items = vm.ListColumns.Take(3).Cast<object>().Concat(trigger.Cells.Take(3)).Concat(trigger.Cells.SelectMany(c => c.Tags)).ToList();
            Assert.Equal(8, items.Count);
            Assert.All(items, item => Assert.False(item.ToString().StartsWith(item.GetType().ToString(), StringComparison.Ordinal), item.GetType().Name));
        }

        // ── the panels' arithmetic ─────────────────────────────────────

        /// <summary>The columns' widths: a column of a set width keeps it,
        /// and the rest share what is left, never under the least.</summary>
        [Fact]
        public void TheCellsShareTheRoomTheSetWidthsLeave()
        {
            double free = double.NaN;
            Assert.Equal(new[] { 225.0, 225.0, 150.0 }, ListCellsPanel.Widths(new[] { free, free, 150.0 }, 600, 180));
            Assert.Equal(new[] { 180.0, 180.0, 150.0 }, ListCellsPanel.Widths(new[] { free, free, 150.0 }, 400, 180));
            Assert.Equal(new[] { 180.0, 180.0 }, ListCellsPanel.Widths(new[] { free, free }, double.PositiveInfinity, 180));
            Assert.Equal(new[] { 100.0, 0.0 }, ListCellsPanel.Widths(new[] { 100.0, -4.0 }, 600, 180));
            Assert.Empty(ListCellsPanel.Widths(Array.Empty<double>(), 600, 180));
        }

        /// <summary>A level's bar: filled from the start to the level, or
        /// from the middle either way for a value that rests there.</summary>
        [Fact]
        public void ALevelFillsFromTheStart_OrFromTheMiddleEitherWay()
        {
            Assert.Equal((0.0, 50.0), LevelBar.FillOf(0.5, false, 100));
            Assert.Equal((0.0, 100.0), LevelBar.FillOf(1.5, false, 100));
            Assert.Equal((0.0, 0.0), LevelBar.FillOf(-1, false, 100));
            Assert.Equal((50.0, 25.0), LevelBar.FillOf(0.75, true, 100));
            Assert.Equal((25.0, 25.0), LevelBar.FillOf(0.25, true, 100));
            Assert.Equal((50.0, 0.0), LevelBar.FillOf(0.5, true, 100));
            Assert.Equal((0.0, 0.0), LevelBar.FillOf(double.NaN, true, 100));
            Assert.Equal((0.0, 0.0), LevelBar.FillOf(0.5, true, 0));

            // A row's bar: a stick's axis rests in the middle and moves by its sign.
            var stick = new MappingItem("LX", "LeftThumbAxisX", MappingCategory.LeftStick);
            Assert.True(stick.IsCenteredValue);
            stick.LiveLevel = 0.5;
            Assert.Equal(0.5, stick.LiveBarLevel);
            stick.LiveSign = 1;
            Assert.Equal(0.75, stick.LiveBarLevel);
            stick.LiveSign = -1;
            Assert.Equal(0.25, stick.LiveBarLevel);
            var trigger = new MappingItem("RT", "RightTrigger", MappingCategory.Triggers);
            Assert.False(trigger.IsCenteredValue);
            trigger.LiveLevel = 0.62;
            trigger.LiveSign = -1;
            Assert.Equal(0.62, trigger.LiveBarLevel);
        }

        /// <summary>A line's plan: the device and the mode from the start,
        /// Remove against the end, the switches before it, and the input in
        /// what is left. A switch that is not drawn leaves its column to
        /// the input. In a narrow box the switches go under the source, and
        /// the input too when the line has no room for it.</summary>
        [Fact]
        public void ALinesPartsStandInTheSameColumns_AndANarrowBoxMovesTheSwitchesUnder()
        {
            const double Device = SourceLinePanel.DeviceWidth, Mode = SourceLinePanel.ModeWidth;
            var wide = SourceLinePanel.PlanFor(1200, narrow: false, letters: false, invert: true, half: true, either: true);
            Assert.Equal((0.0, Device, Mode), (wide.Letter, wide.Device, wide.Mode));
            Assert.Equal(1200 - SourceLinePanel.RemoveWidth, wide.RemoveStart);
            Assert.Equal(wide.RemoveStart - SourceLinePanel.EitherWidth, wide.EitherStart);
            Assert.Equal(wide.EitherStart - SourceLinePanel.HalfWidth, wide.HalfStart);
            Assert.Equal(wide.HalfStart - SourceLinePanel.InvertWidth, wide.InvertStart);
            Assert.Equal((Device + Mode, wide.InvertStart - Device - Mode), (wide.SourceStart, wide.SourceWidth));
            Assert.True(wide.SourceOnLine && wide.SwitchesOnLine);
            // What stands under the source starts under Mode.
            Assert.Equal(Device, wide.UnderStart);

            // With letters every column stands one letter further in.
            var lettered = SourceLinePanel.PlanFor(1200, false, letters: true, true, true, true);
            Assert.Equal(SourceLinePanel.LetterWidth, lettered.Letter);
            Assert.Equal(wide.SourceStart + SourceLinePanel.LetterWidth, lettered.SourceStart);
            Assert.Equal(wide.SourceWidth - SourceLinePanel.LetterWidth, lettered.SourceWidth);
            Assert.Equal(wide.UnderStart + SourceLinePanel.LetterWidth, lettered.UnderStart);

            // A switch that is not drawn leaves its column to the input.
            var keys = SourceLinePanel.PlanFor(1200, false, false, invert: false, half: false, either: false);
            Assert.Equal(wide.RemoveStart - wide.SourceStart, keys.SourceWidth);

            // The width under which a box is narrow: where a full line would leave the input under its least.
            double least = SourceLinePanel.NarrowBelow(false);
            Assert.Equal(SourceLinePanel.LetterWidth, SourceLinePanel.NarrowBelow(true) - least);
            Assert.True(SourceLinePanel.PlanFor(least, false, false, true, true, true).SourceWidth >= SourceLinePanel.SourceLeast);
            Assert.True(SourceLinePanel.PlanFor(least - 1, false, false, true, true, true).SourceWidth < SourceLinePanel.SourceLeast);

            // Narrow: no switch on the line, a narrower device, and the input on the line while it has its least.
            var narrow = SourceLinePanel.PlanFor(800, narrow: true, false, true, true, true);
            Assert.False(narrow.SwitchesOnLine);
            Assert.Equal(SourceLinePanel.NarrowDeviceWidth, narrow.Device);
            Assert.True(narrow.SourceOnLine);
            Assert.Equal((SourceLinePanel.NarrowDeviceWidth + Mode, 800 - SourceLinePanel.RemoveWidth - SourceLinePanel.NarrowDeviceWidth - Mode), (narrow.SourceStart, narrow.SourceWidth));
            var narrower = SourceLinePanel.PlanFor(700, narrow: true, false, true, true, true);
            Assert.False(narrower.SourceOnLine);
            Assert.Equal((SourceLinePanel.NarrowDeviceWidth, 700 - SourceLinePanel.NarrowDeviceWidth), (narrower.SourceStart, narrower.SourceWidth));

            // The box says it is narrow from its own width, and the lines inside read it.
            string box = ExtrasPageTests.RepoText("PadForge.App", "Controls", "SourceLinePanel.cs");
            Assert.Contains("bool narrow = inner > 0 && inner < SourceLinePanel.NarrowBelow(SourceLinePanel.GetLetters(this));", box);
            Assert.Contains("FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure", box);
        }

        // ── the bar under the picture ──────────────────────────────────

        /// <summary>The bar under the picture, as its board draws it: a
        /// head with the picked control's name and its kind, a segment for
        /// each of the control's rows where it has more than one, and Close
        /// at the end. Under the head, the chosen row's lines, the list's
        /// own.</summary>
        [Fact]
        public void TheBarUnderThePictureIsTheControlsHeadAndItsRowsLines()
        {
            var page = Page();
            var bar = Named(page, "PictureBar");
            Assert.Equal("Border", bar.Name.LocalName);
            Assert.Equal("2", (string)bar.Attribute("Grid.Row"));
            Assert.Equal("{Binding ShowPictureBar" + Shown, (string)bar.Attribute("Visibility"));
            var parts = Kids(Assert.Single(Kids(bar)));
            Assert.Equal(new[] { "PickedHead", "PickedLines" }, parts.Select(Name));

            var head = Assert.Single(Kids(parts[0]));
            Assert.Equal("DockPanel", head.Name.LocalName);
            // Close is written first to stand at the end, and Tab takes the segments before it.
            Assert.Equal("Local", Attached(head, "KeyboardNavigation.TabNavigation"));
            var written = Kids(head);
            Assert.Equal(new[] { "PickedLetGo", "PickedNameText", "PickedKindText", null }, written.Select(Name));
            var close = written[0];
            Assert.Equal(("PickedLetGo_Click", "1", "Pad_PickedLetGo"), ((string)close.Attribute("Click"), (string)close.Attribute("TabIndex"), Id(close)));
            Assert.Equal("{Binding Common_Close" + S, (string)close.Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding Common_Close" + S, (string)close.Attribute("ToolTip"));
            Assert.Equal(("{Binding PickedName}", "15", "SemiBold"), ((string)written[1].Attribute("Text"), (string)written[1].Attribute("FontSize"), (string)written[1].Attribute("FontWeight")));
            Assert.Equal("{Binding PickedKind}", (string)written[2].Attribute("Text"));
            Assert.Equal("{Binding PickedKind, Converter={StaticResource StringToVisibility}}", (string)written[2].Attribute("Visibility"));

            // The control's rows: a segment each, and none for a control of one row.
            var track = written[3];
            Assert.Equal(("Local", "0"), (Attached(track, "KeyboardNavigation.TabNavigation"), Attached(track, "KeyboardNavigation.TabIndex")));
            Assert.Equal("{Binding Pad_Picked_Row" + S, (string)track.Attribute("AutomationProperties.Name"));
            Assert.Equal("{StaticResource SegmentTrack}", (string)Assert.Single(track.Descendants(), e => e.Name.LocalName == "Style").Attribute("BasedOn"));
            Assert.Equal(new[] { "0", "1" }, track.Descendants().Where(e => e.Name.LocalName == "DataTrigger").Select(t => (string)t.Attribute("Value")));
            var blocks = Named(track, "PickedBlocks");
            Assert.Equal("{Binding PickedRowsView}", (string)blocks.Attribute("ItemsSource"));
            var segment = Assert.Single(blocks.Descendants(), e => e.Name.LocalName == "RadioButton");
            Assert.Equal(("{StaticResource SegmentRadio}", "{Binding BarLabel}", "{Binding IsRowSelected, Mode=OneWay}", "PickedRow_Click"),
                ((string)segment.Attribute("Style"), (string)segment.Attribute("Content"), (string)segment.Attribute("IsChecked"), (string)segment.Attribute("Click")));

            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Matches(@"private void PickedRow_Click\(object sender, RoutedEventArgs e\)\s*\{\s*if \(\(sender as FrameworkElement\)\?\.DataContext is MappingItem row && DataContext is PadViewModel vm\)\s*vm\.PickedTarget = row\.TargetSettingName;", code);
            Assert.Contains("private void PickedLetGo_Click(object sender, RoutedEventArgs e) => LetGoOfThePick();", code);
            // The picture is sized from the bar's height with one plain source, measured or its base.
            Assert.Contains("private double _barMeasured = PictureBarBaseHeight;", code);

            // A plain row: one source read directly, with nothing under it and nothing beside Add Source.
            var plain = new MappingItem("A", "ButtonA", MappingCategory.Buttons);
            plain.LoadDescriptor("Button 0");
            Assert.True(PadPage.IsPlainOneSource(plain));
            plain.ExtraSources.Add(new MappingSourceItem { Kind = "Direct", Descriptor = "Button 1" });
            Assert.False(PadPage.IsPlainOneSource(plain));
            Assert.False(PadPage.IsPlainOneSource(null));
        }
    }

    /// <summary>
    /// Where the mappings list is built. A change to a row or a device
    /// marks the list, and one build is posted for however many changes
    /// ask for it. The build is posted only from the app's own thread,
    /// which is where the program changes rows. Posted from any other
    /// thread, it would read the rows on the app's thread while that
    /// other thread still changes them. A list changed elsewhere is
    /// built for its next reader.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class PadMappingListThreadTests
    {
        [Fact]
        public void TheListIsBuiltOnTheAppsThread_AndForItsReaderOnAnyOther()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                static PadViewModel.MappedDeviceInfo Pad()
                    => new PadViewModel.MappedDeviceInfo { InstanceGuid = Guid.NewGuid(), Name = "Pad", TypeKey = "Gamepad", IsOnline = true };
                void RunWhatIsPosted() => dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

                // On the app's thread: nothing is built at the change, and
                // the posted build has run by the next idle moment.
                var here = new PadViewModel(0);
                here.MappedDevices.Add(Pad());
                Assert.Empty(here.ListColumns);
                RunWhatIsPosted();
                Assert.Equal(new[] { ListColumnKind.Device, ListColumnKind.Fold }, here.ListColumns.Select(c => c.Kind));
                // One build for a run of changes: a second device, and the columns follow.
                here.MappedDevices.Add(Pad());
                here.MappedDevices.Add(Pad());
                RunWhatIsPosted();
                Assert.Equal(4, here.ListColumns.Count);

                // On another thread: nothing reaches the app's thread.
                PadViewModel there = null;
                var other = new System.Threading.Thread(() =>
                {
                    there = new PadViewModel(1);
                    there.MappedDevices.Add(Pad());
                }) { IsBackground = true };
                other.Start();
                Assert.True(other.Join(20000), "the other thread timed out");
                RunWhatIsPosted();
                Assert.Empty(there.ListColumns);
                // The list is built for the one who reads it next.
                Assert.Equal(2, there.Mappings[0].Cells.Count);
                Assert.Equal(new[] { ListColumnKind.Device, ListColumnKind.Fold }, there.ListColumns.Select(c => c.Kind));
            }, TimeSpan.FromMinutes(2), "the list's thread run timed out");
        }
    }
}
