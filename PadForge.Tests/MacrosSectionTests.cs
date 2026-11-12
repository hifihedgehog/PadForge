using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Macros and Menus section of a controller's page, as its board
    /// draws it (#574): a card of the slot's macros and menus, and the
    /// editor of the one picked in a card beside it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MacrosSectionTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string S = ", Source={x:Static strings:Strings.Instance}}";

        private static XDocument Page() => XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));

        private static string Code() => ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");

        private static string Name(XElement e) => (string)e.Attribute(X + "Name");

        private static XElement Named(XContainer scope, string name)
            => Assert.Single(scope.Descendants(), e => Name(e) == name);

        private static XElement ById(XContainer scope, string id)
            => Assert.Single(scope.Descendants(), e => (string)e.Attribute("AutomationProperties.AutomationId") == id);

        private static string SetterOf(XElement style, string property)
            => (string)Assert.Single(style.Elements(), e => e.Name.LocalName == "Setter"
                && (string)e.Attribute("Property") == property).Attribute("Value");

        private static XElement Keyed(XDocument page, string key)
            => Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Key") == key);

        /// <summary>One item is picked across the two lists: a list shows
        /// its selected item only while its part shows. Picking an item
        /// selects it and shows its part, and a list that loses its pick
        /// to the other hands back nothing, which changes no selection. A
        /// macro made while the section shows opens in the macro editor,
        /// and one made off the section moves no page.</summary>
        [Fact]
        public void OneItemIsPickedAcrossTheTwoLists()
        {
            Assert.Equal(1, PadViewModel.MacrosTabIndex);
            Assert.Equal(15, PadViewModel.MenusTabIndex);
            Assert.Equal(new[] { PadViewModel.MacrosTabIndex, PadViewModel.MenusTabIndex },
                PadViewModel.TabsOfSection(PadViewModel.MacrosSection));

            var vm = new PadViewModel(0);
            var first = new MacroItem { Name = "First" };
            var second = new MacroItem { Name = "Second" };
            vm.Macros.Add(first);
            vm.Macros.Add(second);
            var menu = new MenuEditorItem(new PadForge.Engine.Menus.MenuDefinitionEntry { MenuId = 1, Name = "Wheel" });
            vm.Menus.Add(menu);
            vm.SelectedMenu = menu;
            vm.SelectedConfigTab = PadViewModel.MacrosTabIndex;
            vm.SelectedMacro = first;
            Assert.Same(first, vm.ListedMacro);
            Assert.Null(vm.ListedMenu);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.ListedMacro = second;
            Assert.Same(second, vm.SelectedMacro);
            Assert.Same(second, vm.ListedMacro);
            Assert.Contains(nameof(PadViewModel.ListedMacro), raised);

            // A menu is picked: the macros' list has no pick, and the
            // selected macro stays for when the macros show again.
            raised.Clear();
            vm.ListedMenu = menu;
            Assert.Equal(1, vm.MacrosPartIndex);
            Assert.Equal(PadViewModel.MenusTabIndex, vm.SelectedConfigTab);
            Assert.Same(menu, vm.ListedMenu);
            Assert.Null(vm.ListedMacro);
            Assert.Same(second, vm.SelectedMacro);
            Assert.Contains(nameof(PadViewModel.MacrosPartIndex), raised);
            Assert.Contains(nameof(PadViewModel.ListedMacro), raised);
            Assert.Contains(nameof(PadViewModel.ListedMenu), raised);

            // The macros' list hands back nothing, which selects nothing.
            vm.ListedMacro = null;
            Assert.Same(second, vm.SelectedMacro);
            Assert.Equal(1, vm.MacrosPartIndex);
            vm.ListedMenu = null;
            Assert.Same(menu, vm.SelectedMenu);

            // Picking the macro that was selected shows the macros again.
            vm.ListedMacro = second;
            Assert.Equal(0, vm.MacrosPartIndex);
            Assert.Same(second, vm.ListedMacro);
            Assert.Null(vm.ListedMenu);
            Assert.Same(menu, vm.SelectedMenu);

            // The part's head shows it too.
            vm.SelectedConfigTab = PadViewModel.MenusTabIndex;
            Assert.Same(menu, vm.ListedMenu);

            // A macro made while the menus show opens in the macro editor.
            vm.AddMacroCommand.Execute(null);
            Assert.Equal(0, vm.MacrosPartIndex);
            Assert.Same(vm.Macros.Last(), vm.SelectedMacro);
            Assert.Same(vm.Macros.Last(), vm.ListedMacro);

            // Off the section a macro made by code moves no page.
            vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
            vm.AddMacroCommand.Execute(null);
            Assert.Equal(PadViewModel.MappingsTabIndex, vm.SelectedConfigTab);
            Assert.Null(vm.ListedMenu);

            // A removed macro leaves the list on the one selected next.
            vm.SelectedConfigTab = PadViewModel.MacrosTabIndex;
            raised.Clear();
            vm.RemoveMacroCommand.Execute(null);
            Assert.Same(vm.SelectedMacro, vm.ListedMacro);
            Assert.Contains(nameof(PadViewModel.ListedMacro), raised);
        }

        /// <summary>A menu's style in words, which its list shows at the
        /// end of its item, follows the menu's style.</summary>
        [Fact]
        public void AMenusStyleIsNamedInItsList()
        {
            var menu = new MenuEditorItem(new PadForge.Engine.Menus.MenuDefinitionEntry { MenuId = 1, Name = "Wheel" });
            Assert.Equal(menu.KindOptions.Single(o => o.Value == 0).Label, menu.KindLabel);
            var raised = new List<string>();
            menu.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            menu.KindIndex = 3;
            Assert.Equal(menu.KindOptions.Single(o => o.Value == 3).Label, menu.KindLabel);
            Assert.Contains(nameof(MenuEditorItem.KindLabel), raised);
            Assert.NotEqual(menu.KindOptions.Single(o => o.Value == 0).Label, menu.KindLabel);
        }

        /// <summary>The card of the two lists, as the board draws it: each
        /// list under a head that is its part's switch, New at the head's
        /// end, and the macros' Copy, Paste and Copy From under More. An
        /// item is a dot, its name and, at its end, a macro's layer scope
        /// or a menu's style.</summary>
        [Fact]
        public void TheListsStandInOneCard_UnderTheirHeads()
        {
            var page = Page();
            var card = Named(page, "MacrosListCard");
            Assert.Equal("{StaticResource CardBorder}", (string)card.Attribute("Style"));
            Assert.Equal("6", (string)card.Attribute("Padding"));

            // The heads: the part's switch, with the tab's number.
            foreach (var (head, tab, id, key) in new[]
            {
                ("TabMacros", "1", "MacrosTab", "Pad_Macros"),
                ("TabMenus", "15", "MenusTab", "Pad_Menus"),
            })
            {
                var button = Named(card, head);
                Assert.Equal("RadioButton", button.Name.LocalName);
                Assert.Equal(tab, (string)button.Attribute("Tag"));
                Assert.Equal(id, (string)button.Attribute("AutomationProperties.AutomationId"));
                Assert.Equal("PadMacrosPart", (string)button.Attribute("GroupName"));
                Assert.Equal("{Binding " + key + S, (string)Assert.Single(button.Elements()).Attribute("Text"));
            }
            var headLook = Keyed(page, "ListHeadSwitch");
            Assert.Equal("12", SetterOf(headLook, "FontSize"));
            Assert.Equal("SemiBold", SetterOf(headLook, "FontWeight"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", SetterOf(headLook, "Foreground"));

            // New Macro and New Menu, 28 square at the head's end.
            Assert.Equal("{Binding AddMacroCommand}", (string)ById(card, "MacroAddButton").Attribute("Command"));
            Assert.Equal("{Binding AddMenuCommand}", (string)ById(card, "MenuAddButton").Attribute("Command"));
            foreach (string id in new[] { "MacroAddButton", "MenuAddButton", "MacroListMoreButton" })
                Assert.Equal("{StaticResource ListHeadButton}", (string)ById(card, id).Attribute("Style"));
            Assert.Equal("28", SetterOf(Keyed(page, "ListHeadButton"), "Width"));
            Assert.Equal("28", SetterOf(Keyed(page, "ListHeadButton"), "Height"));

            // The macros' More: Copy, Paste and Copy From.
            var more = ById(card, "MacroListMoreButton");
            Assert.Equal("MoreMenu_Click", (string)more.Attribute("Click"));
            Assert.Equal(new[] { "{Binding CopyMacroCommand}", "{Binding PasteMacroCommand}", "{Binding CopyMacroFromCommand}" },
                more.Descendants().Where(e => e.Name.LocalName == "MenuItem").Select(e => (string)e.Attribute("Command")));

            // The lists: the item picked across both, with no scroll view
            // of their own.
            var lists = card.Descendants().Where(e => e.Name.LocalName == "ListBox").ToList();
            Assert.Equal(new[] { "{Binding Macros}", "{Binding Menus}" }, lists.Select(l => (string)l.Attribute("ItemsSource")));
            Assert.Equal(new[] { "{Binding ListedMacro, Mode=TwoWay}", "{Binding ListedMenu, Mode=TwoWay}" },
                lists.Select(l => (string)l.Attribute("SelectedItem")));
            Assert.All(lists, l =>
            {
                Assert.Equal("{StaticResource PartList}", (string)l.Attribute("Style"));
                Assert.Equal("{StaticResource PartListItem}", (string)l.Attribute("ItemContainerStyle"));
                Assert.Contains(l.Descendants(), e => (string)e.Attribute("Text") == "{Binding Name}");
            });
            var listLook = Keyed(page, "PartList");
            Assert.Equal("ItemsPresenter", Assert.Single(Assert.Single(listLook.Descendants(), e => e.Name.LocalName == "ControlTemplate").Elements()).Name.LocalName);
            // An item: 38 tall, read by its name, the picked one on the
            // ember fill.
            var item = Keyed(page, "PartListItem");
            Assert.Equal("38", SetterOf(item, "MinHeight"));
            Assert.Equal("12,0", SetterOf(item, "Padding"));
            Assert.Equal("{Binding Name, TargetNullValue=''}", SetterOf(item, "AutomationProperties.Name"));
            var picked = Assert.Single(item.Descendants(), e => e.Name.LocalName == "Trigger" && (string)e.Attribute("Property") == "IsSelected");
            Assert.Contains(picked.Elements(), e => (string)e.Attribute("Value") == "{DynamicResource EmberCardPickBrush}");
            // The dot is quiet on one that is off, and a macro on some
            // layers only says so (#254 A-1).
            Assert.Equal(new[] { "{Binding IsEnabled}", "{Binding Enabled}" },
                lists.Select(l => (string)Assert.Single(l.Descendants(), e => e.Name.LocalName == "DataTrigger").Attribute("Binding")));
            Assert.Contains(lists[0].Descendants(), e => (string)e.Attribute("Visibility") == "{Binding HasLayerScope, Converter={StaticResource BoolToVisibilityConverter}}");
            Assert.Contains(lists[1].Descendants(), e => (string)e.Attribute("Text") == "{Binding KindLabel}");

            // The old tools are gone from the section: no splitter, and no
            // row of list buttons.
            var section = Named(page, "MacrosSectionScroll");
            Assert.DoesNotContain(section.Descendants(), e => e.Name.LocalName == "GridSplitter");
            Assert.DoesNotContain(section.Descendants(), e => e.Name.LocalName == "Button"
                && (string)e.Attribute("Command") is "{Binding RemoveMacroCommand}" or "{Binding DuplicateMacroCommand}" or "{Binding RemoveMenuCommand}" or "{Binding DuplicateMenuCommand}");
        }

        /// <summary>An editor's head, as the board draws it: the name at 20
        /// SemiBold, typed over in place, with a pencil that puts the caret
        /// in it, then Enabled as a switch and More, which holds Duplicate,
        /// Copy and Remove.</summary>
        [Fact]
        public void AnEditorsHeadIsItsName_Enabled_AndMore()
        {
            var page = Page();
            var editor = Named(page, "MacrosEditorCard");
            Assert.Equal("0", (string)editor.Attribute("Padding"));
            var parts = Assert.Single(editor.Elements()).Elements().Where(e => e.Name.LocalName == "TabItem").ToList();
            Assert.Equal(2, parts.Count);

            foreach (var (part, box, nameKey, selected, has, enabled, enabledKey, moreId, commands, removeId) in new[]
            {
                (parts[0], "MacroNameBox", "Macro_Name", "SelectedMacro", "HasSelectedMacro", "{Binding IsEnabled, Mode=TwoWay}", "Common_Enabled", "MacroMoreButton",
                    new[] { "{Binding DuplicateMacroCommand}", "{Binding CopyMacroCommand}", "{Binding RemoveMacroCommand}" }, "MacroRemoveButton"),
                (parts[1], "MenuNameBox", "Menu_Name", "SelectedMenu", "HasSelectedMenu", "{Binding Enabled}", "Menu_Enabled", "MenuMoreButton",
                    new[] { "{Binding DuplicateMenuCommand}", "{Binding RemoveMenuCommand}" }, "MenuRemoveButton"),
            })
            {
                // The editor shows while its part has a selection, on that
                // selection.
                var host = Assert.Single(part.Descendants(), e => (string)e.Attribute("DataContext") == "{Binding " + selected + "}");
                Assert.StartsWith("{Binding DataContext." + has + ",", (string)host.Attribute("Visibility"));
                Assert.Equal(selected == "SelectedMacro" ? "MacroEditorHost" : "MenuEditorHost", Name(host));

                var name = Named(host, box);
                Assert.Equal("TextBox", name.Name.LocalName);
                Assert.Equal("{StaticResource HeadNameEdit}", (string)name.Attribute("Style"));
                Assert.Equal("{Binding Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}", (string)name.Attribute("Text"));
                Assert.Equal("{Binding " + nameKey + S, (string)name.Attribute("AutomationProperties.Name"));
                var pencil = Assert.Single(host.Descendants(), e => (string)e.Attribute("Click") == "RenameName_Click");
                Assert.Equal("{Binding ElementName=" + box + "}", (string)pencil.Attribute("Tag"));
                Assert.Same(name.Parent, pencil.Parent);
                Assert.Equal("FlexLinePanel", name.Parent!.Name.LocalName);

                var onOff = Assert.Single(host.Descendants(), e => e.Name.LocalName == "CheckBox" && (string)e.Attribute("IsChecked") == enabled);
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)onOff.Attribute("Style"));
                Assert.Equal("{Binding " + enabledKey + S, (string)onOff.Attribute("Content"));
                Assert.Same(name.Parent.Parent, onOff.Parent);

                var more = ById(host, moreId);
                Assert.Equal("MoreMenu_Click", (string)more.Attribute("Click"));
                Assert.Equal("{StaticResource CardIconButton}", (string)more.Attribute("Style"));
                var items = more.Descendants().Where(e => e.Name.LocalName == "MenuItem").ToList();
                Assert.Equal(commands, items.Select(e => (string)e.Attribute("Command")));
                Assert.Equal(removeId, (string)items.Last().Attribute("AutomationProperties.AutomationId"));
                Assert.Equal("{DynamicResource DangerInkBrush}", (string)items.Last().Attribute("Foreground"));
            }

            var heading = Keyed(page, "HeadNameEdit");
            Assert.Equal("{StaticResource DzValueEdit}", (string)heading.Attribute("BasedOn"));
            Assert.Equal("20", SetterOf(heading, "FontSize"));
            Assert.Equal("SemiBold", SetterOf(heading, "FontWeight"));
            Assert.Equal("Left", SetterOf(heading, "TextAlignment"));

            // A row that reaches its macro or its menu from inside a part of
            // its own asks the editor's host by name. The nearest scroll
            // viewer is the section's, whose data is the page's: the selected
            // action's editor showed with no action selected, and a keyboard
            // menu's Opens With row showed for every menu (seen 2026-11-11).
            var section = Named(page, "MacrosSectionScroll");
            Assert.DoesNotContain(section.Descendants().SelectMany(e => e.Attributes()), a => a.Value.Contains("AncestorType=ScrollViewer", StringComparison.Ordinal));
            var action = Assert.Single(section.Descendants(), e => (string)e.Attribute("DataContext") == "{Binding SelectedAction}");
            Assert.Matches(@"^\{Binding DataContext\.SelectedAction,\s*ElementName=MacroEditorHost,\s*Converter=\{StaticResource NullToCollapsedConverter\}\}$",
                (string)action.Attribute("Visibility"));
            Assert.Contains(section.Descendants(), e => (string)e.Attribute("DataContext") == "{Binding OpenRow}"
                && (string)e.Attribute("Visibility") == "{Binding DataContext.IsKeyboard, ElementName=MenuEditorHost, Converter={StaticResource BoolToVisibilityConverter}}");

            Assert.Matches(@"private void RenameName_Click\(object sender, RoutedEventArgs e\)\s*\{\s*if \(\(sender as FrameworkElement\)\?\.Tag is not TextBox box\) return;\s*box\.Focus\(\);\s*box\.SelectAll\(\);", Code());
        }

        /// <summary>The macro editor's rows stand in the board's blocks:
        /// When holds what fires the macro, Do its actions, and More, a
        /// fold at the card's foot, what a macro seldom needs. Every row
        /// kept the rule that shows it. The rows of the trigger's
        /// combination that went under More took the combination's rule
        /// with them, and Repeat shows by its own.</summary>
        [Fact]
        public void TheMacroEditorsRowsStandInWhen_Do_AndMore()
        {
            var page = Page();
            var host = Named(page, "MacroEditorHost");
            var when = Named(host, "MacroWhenBlock");
            var act = Named(host, "MacroDoBlock");
            var more = Named(host, "MacroMore");
            // In that order under the head, and nothing after More.
            Assert.Equal(new[] { "DockPanel", "Border", "Border", "MoreRow" }, host.Elements().Select(e => e.Name.LocalName));
            Assert.Equal(new[] { when, act, more }, host.Elements().Skip(1));
            foreach (var block in new[] { when, act })
                Assert.Equal("{StaticResource EditorBlock}", (string)block.Attribute("Style"));
            var blockLook = Keyed(page, "EditorBlock");
            Assert.Equal("0,1,0,0", SetterOf(blockLook, "BorderThickness"));
            Assert.Equal("20,14,12,14", SetterOf(blockLook, "Padding"));
            var headLook = Keyed(page, "EditorBlockHead");
            Assert.Equal("13", SetterOf(headLook, "FontSize"));
            Assert.Equal("SemiBold", SetterOf(headLook, "FontWeight"));
            Assert.Equal("{DynamicResource EmberHotBrush}", SetterOf(headLook, "Foreground"));

            const string combination = "{Binding ShowsTriggerComboEditor, Converter={StaticResource BoolToVisibilityConverter}}";
            string Gate(XElement row) => ((string)row.Attribute("Visibility") ?? "").Substring("{Binding ".Length).Split(',')[0];

            // When: the head, the combination's inputs, then the way the
            // macro fires and that way's own settings.
            var whenRows = Assert.Single(when.Elements()).Elements().ToList();
            Assert.Equal(8, whenRows.Count);
            Assert.Equal("{StaticResource EditorBlockHead}", (string)whenRows[0].Attribute("Style"));
            Assert.Equal("{Binding Macro_Trigger" + S, (string)whenRows[0].Attribute("Text"));
            var inputs = whenRows[1];
            Assert.Equal(combination, (string)inputs.Attribute("Visibility"));
            string inputsText = inputs.ToString();
            foreach (string part in new[] { "SlotMacroTriggerChoicesView", "RecordTriggerCommand", "ClearTriggerCommand", "Macro_RecordHint", "RecordingLiveText", "TriggerInputItems" })
                Assert.Contains(part, inputsText);
            Assert.Contains("{Binding TriggerMode, Mode=TwoWay}", whenRows[2].ToString());
            Assert.Null(whenRows[2].Attribute("Visibility"));
            Assert.Equal(new[] { "ShowsHoldTimeRow", "IsDoublePressMode", "ShowsInlineIntervalRow", "IsCustomExpressionMode" },
                whenRows.Skip(3).Take(4).Select(Gate));
            Assert.Equal("FormulaEditor", Name(whenRows[6]));
            Assert.Contains("Macro_AlwaysDesc", whenRows[7].ToString());

            // Do: the head, the actions, their buttons, and the editor of
            // the selected one, which shows only while one is selected.
            var doRows = Assert.Single(act.Elements()).Elements().ToList();
            Assert.Equal(4, doRows.Count);
            Assert.Equal("{Binding Macro_ActionSequence" + S, (string)doRows[0].Attribute("Text"));
            Assert.Contains(doRows[1].Descendants(), e => Name(e) == "ActionsList");
            Assert.Contains("AddActionCommand", doRows[2].ToString());
            Assert.Equal("{Binding SelectedAction}", (string)doRows[3].Attribute("DataContext"));
            Assert.Contains("ElementName=MacroEditorHost", (string)doRows[3].Attribute("Visibility"));

            // More: the layer, the rest of the combination under the
            // combination's rule, and Repeat under its own.
            Assert.Equal("Pad.Macros.More", (string)more.Attribute("StateKey"));
            Assert.Equal("0", (string)more.Attribute("Margin"));
            var moreRows = Assert.Single(more.Elements(), e => e.Name.LocalName == "StackPanel").Elements().ToList();
            // The layer, the rest of the combination, then Repeat: its
            // head, the mode, the count and the delay.
            Assert.Equal(6, moreRows.Count);
            Assert.Contains("{Binding LayerMask, Mode=TwoWay}", moreRows[0].ToString());
            var rest = moreRows[1];
            Assert.Equal(combination, (string)rest.Attribute("Visibility"));
            string[] kept = { "TriggerSource", "TriggerAxisThreshold", "TriggerAxisDirectionIndex", "ConsumeTriggerButtons" };
            Assert.Equal(kept, rest.Elements().Select(r => kept.First(k => r.ToString().Contains(k, StringComparison.Ordinal))));
            // The old axis trigger's two rows show only for a trigger that
            // has one.
            Assert.All(rest.Elements().Skip(1).Take(2), r => Assert.Equal("HasLegacyAxisTrigger", Gate(r)));
            Assert.All(moreRows.Skip(2), r => Assert.Equal("ShowsRepeatSection", Gate(r)));

            // The combination's rule stands on those two groups and nowhere
            // else.
            Assert.Equal(2, host.Descendants().Count(e => (string)e.Attribute("Visibility") == combination));
        }

        /// <summary>A row of a setting in the macro editor, as the board
        /// draws it: 44 tall, a label of 190 with 12 after it, the
        /// control, and the reset 10 after that at the row's end. The section gives its
        /// rows that look. A reset carries its end mark and its margin as
        /// its own attributes.</summary>
        [Fact]
        public void AMacroRowIsItsLabel_ItsControl_AndItsResetAtTheEnd()
        {
            var page = Page();
            var section = Named(page, "MacrosSectionScroll");
            var looks = Assert.Single(section.Elements(), e => e.Name.LocalName == "ScrollViewer.Resources");
            XElement Look(string key) => Assert.Single(looks.Elements(), e => (string)e.Attribute(X + "Key") == key);
            var line = Look("RowOptionLine");
            Assert.Equal("controls:FlexLinePanel", (string)line.Attribute("TargetType"));
            Assert.Equal("44", SetterOf(line, "MinLineHeight"));
            Assert.Equal("True", SetterOf(line, "EndsApart"));
            var label = Look("RowOptionLabel");
            Assert.Equal("{StaticResource RowOptionLabel}", (string)label.Attribute("BasedOn"));
            Assert.Equal("190", SetterOf(label, "Width"));
            Assert.Equal("0,0,12,0", SetterOf(label, "Margin"));
            Assert.Equal("10,0,0,0", Look("RowOptionResetMargin").Value);

            var host = Named(page, "MacroEditorHost");
            var rows = host.Descendants().Where(e => e.Name.LocalName == "FlexLinePanel"
                && (string)e.Attribute("Style") == "{DynamicResource RowOptionLine}").ToList();
            // Fire, its three own rows, the layer, the source, the old
            // trigger's two, Consume, and Repeat's three. Then the rows of an
            // input of the trigger: an axis's three switches and its
            // deadzone, and the point an input with a depth fires at.
            Assert.Equal(17, rows.Count);
            string[] binds =
            {
                "{Binding TriggerMode, Mode=TwoWay}", "{Binding TriggerHoldMs, Mode=TwoWay, UpdateSourceTrigger=LostFocus}",
                "{Binding TriggerDoublePressMs, Mode=TwoWay, UpdateSourceTrigger=LostFocus}", "{Binding RepeatDelayMs, Mode=TwoWay, UpdateSourceTrigger=LostFocus}",
                "{Binding LayerMask, Mode=TwoWay}", "{Binding TriggerSource, Mode=TwoWay}", "{Binding TriggerAxisThreshold, Mode=TwoWay}",
                "{Binding TriggerAxisDirectionIndex, Mode=TwoWay}", "{Binding ConsumeTriggerButtons, Mode=TwoWay}", "{Binding RepeatMode, Mode=TwoWay}",
                "{Binding RepeatCount, Mode=TwoWay}", "{Binding RepeatDelayMs, Mode=TwoWay}",
                "{Binding AxisEntry.Invert, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
                "{Binding AxisEntry.HalfAxis, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
                "{Binding AxisEntry.Bidirectional, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
                "{Binding AxisEntry.DeadZone, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
                "{Binding ThresholdEntry.DescriptorThresholdPercent, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            };
            foreach (string bound in binds)
                Assert.Contains(rows, r => r.Elements().Any(c => c.Attributes().Any(a => a.Value == bound)));
            Assert.All(rows, r =>
            {
                var parts = r.Elements().ToList();
                Assert.Equal("TextBlock", parts[0].Name.LocalName);
                Assert.Equal("{DynamicResource RowOptionLabel}", (string)parts[0].Attribute("Style"));
                Assert.StartsWith("{Binding ", (string)parts[0].Attribute("Text"));
                // The reset is the row's last part, marked as its end part
                // at the rows' margin.
                var reset = parts[^1];
                Assert.True(reset.Name.LocalName == "SettingResetButton" || (string)reset.Attribute("Style") == "{StaticResource ResetButton}",
                    "a row ends with " + reset.Name.LocalName);
                Assert.Equal("True", (string)reset.Attributes().First(a => a.Name.LocalName == "FlexLinePanel.AtEnd"));
                Assert.Equal("{DynamicResource RowOptionResetMargin}", (string)reset.Attribute("Margin"));
                // A picker or a box stands centered in the row's height: the
                // row would stretch it to its 44. The page's value text is
                // centered by its style.
                Assert.All(parts.Where(c => c.Name.LocalName is "ComboBox" or "TextBox"), c => Assert.True(
                    (string)c.Attribute("VerticalAlignment") == "Center" || (string)c.Attribute("Style") == "{StaticResource DzValueEdit}",
                    "a row's " + c.Name.LocalName + " is not centered"));
            });
            // No old row of the editor is left beside them: a horizontal
            // strip with the old 80 wide label.
            Assert.DoesNotContain(Named(host, "MacroMore").Descendants(), e => e.Name.LocalName == "TextBlock" && (string)e.Attribute("Width") == "80");

            // Consume Trigger Buttons is a switch under its own long label.
            var consume = rows.Single(r => r.Elements().Any(c => (string)c.Attribute("IsChecked") == "{Binding ConsumeTriggerButtons, Mode=TwoWay}"));
            Assert.Equal("320", (string)consume.Elements().First().Attribute("Width"));
            Assert.Equal("{StaticResource SwitchCheckBox}", (string)consume.Elements().Single(c => c.Name.LocalName == "CheckBox").Attribute("Style"));
            // Count and Delay are a row each, and Delay has the interval's
            // reset.
            var delay = rows.Single(r => r.Elements().Any(c => (string)c.Attribute("Text") == "{Binding RepeatDelayMs, Mode=TwoWay}"));
            Assert.Equal("{Binding ResetRepeatDelayMsCommand}", (string)delay.Elements().Last().Attribute("Command"));
            Assert.StartsWith("{Binding ShowsRepeatSection,", (string)delay.Attribute("Visibility"));
            // The layer row shows by the holder around it.
            var layer = rows.Single(r => r.Elements().Any(c => (string)c.Attribute("SelectedValue") == "{Binding LayerMask, Mode=TwoWay}"));
            Assert.Null(layer.Attribute("Visibility"));
            var holder = layer.Parent!;
            Assert.Equal("StackPanel", holder.Name.LocalName);
            Assert.Equal(new[] { "{Binding DataContext.HasShiftLayers, RelativeSource={RelativeSource AncestorType=UserControl}}", "{Binding ShowsLayerRow}" },
                holder.Elements().Single(e => e.Name.LocalName == "StackPanel.Style").Descendants()
                    .Where(e => e.Name.LocalName == "DataTrigger").Select(e => (string)e.Attribute("Binding")));
        }

        /// <summary>The inputs of a macro's trigger stand on one line, as
        /// the board draws them: a pill for each with its remove, "and"
        /// between two, then Record and Add from List, and Clear after
        /// them. The line wraps, and its own parts go on from the last
        /// pill. Under it stand the record hint, what is being recorded,
        /// and the settings of each input that has any, as rows.</summary>
        [Fact]
        public void TheTriggersInputsStandOnOneLine_WithRecordAndAddFromListAfterThem()
        {
            var page = Page();
            var when = Named(page, "MacroWhenBlock");
            var line = Named(when, "MacroInputsLine");
            Assert.Equal("FlexLinePanel", line.Name.LocalName);
            Assert.Equal("8", (string)line.Attribute("Gap"));
            Assert.Equal("8", (string)line.Attribute("LineGap"));
            string Lead(string name) => (string)Assert.Single(line.Attributes(), a => a.Name.LocalName == "LeadItems." + name);
            Assert.Equal("{Binding TriggerInputItems}", Lead("Source"));
            Assert.Equal("{StaticResource InputChipPart}", Lead("Template"));
            Assert.Equal("{StaticResource InputChipAnd}", Lead("Between"));

            // The line's own parts, in order: the words of a trigger with
            // no input, Record, Add from List and Clear.
            var own = line.Elements().ToList();
            Assert.Equal(new[] { "TextBlock", "Button", "Grid", "Button" }, own.Select(e => e.Name.LocalName));
            Assert.Equal("{Binding Macro_NotSet" + S, (string)own[0].Attribute("Text"));
            Assert.Equal("{Binding HasTriggerInputItems, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}",
                (string)own[0].Attribute("Visibility"));
            Assert.All(own.Skip(1), part => Assert.Null(part.Attribute("Visibility")));

            // Record, 30 tall with 12 at its sides: the record glyph and the
            // word, which is Stop while it records. Its name says what it
            // records.
            var record = own[1];
            Assert.Equal("{Binding RecordTriggerCommand}", (string)record.Attribute("Command"));
            Assert.Equal("30", (string)record.Attribute("MinHeight"));
            Assert.Equal("12,0", (string)record.Attribute("Padding"));
            Assert.Equal("{Binding RecordTriggerButtonText, TargetNullValue=''}", (string)record.Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding Macro_RecordHint" + S, (string)record.Attribute("ToolTip"));
            Assert.Contains(record.Descendants(), e => (string)e.Attribute("Text") == "{Binding RecordTriggerIcon}");
            var word = Assert.Single(record.Descendants(), e => e.Name.LocalName == "Style");
            Assert.Equal("{Binding Common_Record" + S, SetterOf(word, "Text"));
            var recording = Assert.Single(word.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding IsRecordingTrigger}", (string)recording.Attribute("Binding"));
            Assert.Equal("{Binding Common_Stop" + S, (string)Assert.Single(recording.Elements()).Attribute("Value"));

            // Add from List: the picker with no fill and no edge and its own
            // chevron not drawn, under its face, which takes no click from
            // it: the words and a chevron in the text color. The picker is
            // as wide as that face and as tall as Record.
            var picker = Assert.Single(own[2].Elements(), e => e.Name.LocalName == "ComboBox");
            Assert.Equal("{Binding DataContext.SlotMacroTriggerChoicesView, RelativeSource={RelativeSource AncestorType=UserControl}}",
                (string)picker.Attribute("ItemsSource"));
            Assert.Equal("MacroTriggerPick_SelectionChanged", (string)picker.Attribute("SelectionChanged"));
            Assert.Equal("MacroTriggerPick_PreviewKeyDown", (string)picker.Attribute("PreviewKeyDown"));
            Assert.Equal("False", (string)picker.Attribute("IsTextSearchEnabled"));
            Assert.Equal("Transparent", (string)picker.Attribute("Background"));
            Assert.Equal("Transparent", (string)picker.Attribute("BorderBrush"));
            Assert.Equal("0", (string)picker.Attribute("MinWidth"));
            Assert.Equal("30", (string)picker.Attribute("MinHeight"));
            var ownChevron = Assert.Single(picker.Elements().Single(e => e.Name.LocalName == "ComboBox.Resources").Elements());
            Assert.Equal("ComboBoxDropDownGlyphForeground", (string)ownChevron.Attribute(X + "Key"));
            Assert.Equal("Transparent", (string)ownChevron.Attribute("Color"));
            Assert.Equal("{Binding Macro_AddTriggerFromList" + S, (string)picker.Attribute("AutomationProperties.Name"));
            Assert.Contains(picker.Descendants(), e => e.Name.LocalName == "GroupStyle");
            var face = Assert.Single(own[2].Elements(), e => e.Name.LocalName == "StackPanel");
            Assert.Equal("False", (string)face.Attribute("IsHitTestVisible"));
            Assert.Equal(new[] { picker, face }, own[2].Elements());
            Assert.Equal(new[] { "TextBlock", "Path" }, face.Elements().Select(e => e.Name.LocalName));
            Assert.Equal("{Binding Macro_AddTriggerFromList" + S, (string)face.Elements().First().Attribute("Text"));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", (string)face.Elements().First().Attribute("Foreground"));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", (string)face.Elements().Last().Attribute("Stroke"));

            var clear = own[3];
            Assert.Equal("{Binding ClearTriggerCommand}", (string)clear.Attribute("Command"));
            Assert.Equal("{StaticResource CardIconButton}", (string)clear.Attribute("Style"));
            // As tall as the line's other parts, so the line is the board's 30.
            Assert.Equal("30", (string)clear.Attribute("Width"));
            Assert.Equal("30", (string)clear.Attribute("Height"));
            Assert.Equal("{Binding Common_Clear" + S, (string)clear.Attribute("AutomationProperties.Name"));

            // The pill: 30 tall and round at its ends, 12 at its start and 6
            // at its end, the raised fill inside a cold edge. Its words are
            // cut short on a line too narrow for them, and its remove, a
            // round 22, stands 6 after them.
            var pill = Keyed(page, "InputChip");
            Assert.Equal("30", SetterOf(pill, "MinHeight"));
            Assert.Equal("15", SetterOf(pill, "CornerRadius"));
            Assert.Equal("12,0,6,0", SetterOf(pill, "Padding"));
            Assert.Equal("1", SetterOf(pill, "BorderThickness"));
            Assert.Equal("{DynamicResource InputChipEdgeBrush}", SetterOf(pill, "BorderBrush"));
            Assert.Equal("{DynamicResource SteelRaisedBrush}", SetterOf(pill, "Background"));
            Assert.Equal("{DynamicResource InputChipTextBrush}", SetterOf(pill, "TextElement.Foreground"));
            var removeLook = Keyed(page, "InputChipRemove");
            Assert.Equal("{StaticResource QuietResetButton}", (string)removeLook.Attribute("BasedOn"));
            Assert.Equal("22", SetterOf(removeLook, "Width"));
            Assert.Equal("22", SetterOf(removeLook, "Height"));
            Assert.Equal("11", SetterOf(removeLook, "CornerRadius"));
            Assert.Equal("6,0,0,0", SetterOf(removeLook, "Margin"));
            var part = Keyed(page, "InputChipPart");
            var body = Assert.Single(part.Elements());
            Assert.Equal("{StaticResource InputChip}", (string)body.Attribute("Style"));
            var label = Assert.Single(body.Descendants(), e => e.Name.LocalName == "TextBlock");
            Assert.Equal("{Binding Label}", (string)label.Attribute("Text"));
            Assert.Equal("CharacterEllipsis", (string)label.Attribute("TextTrimming"));
            var remove = Assert.Single(body.Descendants(), e => e.Name.LocalName == "Button");
            Assert.Equal("{StaticResource InputChipRemove}", (string)remove.Attribute("Style"));
            Assert.Equal("{Binding RemoveCommand}", (string)remove.Attribute("Command"));
            Assert.Equal("{Binding Macro_RemoveTriggerInput" + S, (string)remove.Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding Pad_Formula_Status_And" + S, (string)Assert.Single(Keyed(page, "InputChipAnd").Elements()).Attribute("Text"));

            // The pill's two colors, in both themes.
            string app = ExtrasPageTests.RepoText("PadForge.App", "App.xaml");
            Assert.Contains("<SolidColorBrush x:Key=\"InputChipEdgeBrush\" Color=\"#33506A\"/>", app);
            Assert.Contains("<SolidColorBrush x:Key=\"InputChipTextBrush\" Color=\"#CFE6F5\"/>", app);
            string theme = ExtrasPageTests.RepoText("PadForge.App", "Common", "EmberTheme.cs");
            Assert.Contains("SetBrush(\"InputChipEdgeBrush\", dark ? Color.FromRgb(0x33, 0x50, 0x6A) : Color.FromRgb(0x9D, 0xBB, 0xD2));", theme);
            Assert.Contains("SetBrush(\"InputChipTextBrush\", dark ? Color.FromRgb(0xCF, 0xE6, 0xF5) : Color.FromRgb(0x1D, 0x4B, 0x6B));", theme);

            // Under the line: how to record and what is being recorded, both
            // only while it records, and each input's settings. At rest the
            // way the macro fires follows the line, as on the board.
            var group = line.Parent!;
            var under = group.Elements().ToList();
            Assert.Equal(4, under.Count);
            Assert.Same(line, under[0]);
            Assert.Equal("{Binding Macro_RecordHint" + S, (string)under[1].Attribute("Text"));
            Assert.Equal("{Binding RecordingLiveText}", (string)under[2].Attribute("Text"));
            Assert.All(under.Skip(1).Take(2), text => Assert.Equal(
                "{Binding IsRecordingTrigger, Converter={StaticResource BoolToVisibilityConverter}}", (string)text.Attribute("Visibility")));
            var settings = under[3];
            Assert.Equal("ItemsControl", settings.Name.LocalName);
            Assert.Equal("{Binding TriggerInputItems}", (string)settings.Attribute("ItemsSource"));
            var kinds = Assert.Single(settings.Descendants(), e => e.Name.LocalName == "DataTemplate").Elements().Single().Elements().ToList();
            Assert.Equal(new[]
                {
                    "{Binding IsAxis, Converter={StaticResource BoolToVisibilityConverter}}",
                    "{Binding HasThreshold, Converter={StaticResource BoolToVisibilityConverter}}",
                },
                kinds.Select(k => (string)k.Attribute("Visibility")));
            // Each kind's rows stand under the input's name.
            Assert.All(kinds, k => Assert.Equal("{Binding Label}", (string)k.Elements().First().Attribute("Text")));
            Assert.Equal(new[] { 4, 1 }, kinds.Select(k => k.Elements().Count(e => e.Name.LocalName == "FlexLinePanel")));
            Assert.Equal(new[] { "Reset_TriggerInputEntry_Invert_1825", "Reset_TriggerInputEntry_HalfAxis_1828", "Reset_TriggerInputEntry_Bidirectional_1833" },
                kinds[0].Descendants().Select(e => (string)e.Attribute("AutomationProperties.AutomationId")).Where(id => id != null));
            Assert.Contains(kinds[0].Descendants(), e => (string)e.Attribute("Command") == "{Binding AxisEntry.ResetDeadZoneCommand}");
            Assert.Contains(kinds[1].Descendants(), e => (string)e.Attribute("Command") == "{Binding ThresholdEntry.ResetDescriptorDeadZoneCommand}");
            // A slider and its number have their row's name.
            Assert.All(settings.Descendants().Where(e => e.Name.LocalName is "Slider" or "TextBox"),
                e => Assert.Equal("{Binding Pad_Sticks_Section_DeadZone" + S, (string)e.Attribute("AutomationProperties.Name")));

            // The old list of inputs is gone: its well with a scroll view of
            // its own, and the mapping grid's chip.
            Assert.DoesNotContain(when.Descendants(), e => e.Name.LocalName == "ScrollViewer");
            Assert.DoesNotContain(when.Descendants(), e => (string)e.Attribute("Style") == "{StaticResource ChipCold}");
        }
    }
}
