using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Xml.Linq;
using PadForge.ViewModels;
using PadForge.Views;
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
            // Each is named for what it makes, as the board names it.
            foreach (var (id, key) in new[] { ("MacroAddButton", "Macro_NewMacro"), ("MenuAddButton", "Menu_NewMenu") })
            {
                Assert.Equal("{Binding " + key + S, (string)ById(card, id).Attribute("AutomationProperties.Name"));
                Assert.Equal("{Binding " + key + S, (string)ById(card, id).Attribute("ToolTip"));
            }
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
        /// Copy, Clear Trigger and Remove for a macro, and Duplicate, Remove
        /// Last Cell and Remove for a menu.</summary>
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
                    new[] { "{Binding DuplicateMacroCommand}", "{Binding CopyMacroCommand}", "{Binding SelectedMacro.ClearTriggerCommand}", "{Binding RemoveMacroCommand}" }, "MacroRemoveButton"),
                (parts[1], "MenuNameBox", "Menu_Name", "SelectedMenu", "HasSelectedMenu", "{Binding Enabled}", "Menu_Enabled", "MenuMoreButton",
                    new[] { "{Binding DuplicateMenuCommand}", "{Binding SelectedMenu.RemoveLastCellCommand}", "{Binding RemoveMenuCommand}" }, "MenuRemoveButton"),
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
                Assert.Equal("{Binding Common_Rename" + S, (string)pencil.Attribute("AutomationProperties.Name"));
                Assert.Equal("{Binding Common_Rename" + S, (string)pencil.Attribute("ToolTip"));
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

            // Clear Trigger takes every input out of the macro's trigger. Remove
            // Last Cell shows while the menu has cells.
            var clearTrigger = ById(page, "MacroClearTrigger");
            Assert.Equal("MenuItem", clearTrigger.Name.LocalName);
            Assert.Equal("{Binding Macro_ClearTrigger" + S, (string)clearTrigger.Attribute("Header"));
            Assert.Null(clearTrigger.Attribute("Visibility"));
            var removeCell = ById(page, "MenuRemoveCell");
            Assert.Equal("MenuItem", removeCell.Name.LocalName);
            Assert.Equal("{Binding Menu_RemoveLastCell" + S, (string)removeCell.Attribute("Header"));
            Assert.Equal("{Binding SelectedMenu.IsCellMenu, Converter={StaticResource BoolToVisibilityConverter}}", (string)removeCell.Attribute("Visibility"));

            var heading = Keyed(page, "HeadNameEdit");
            Assert.Equal("{StaticResource DzValueEdit}", (string)heading.Attribute("BasedOn"));
            Assert.Equal("20", SetterOf(heading, "FontSize"));
            Assert.Equal("SemiBold", SetterOf(heading, "FontWeight"));
            Assert.Equal("Left", SetterOf(heading, "TextAlignment"));

            // No row reaches its macro or its menu through the nearest scroll
            // viewer, which is the section's, whose data is the page's: the
            // selected action's editor showed with no action selected, and a
            // keyboard menu's Opens With row showed for every menu (seen
            // 2026-11-11).
            var section = Named(page, "MacrosSectionScroll");
            Assert.DoesNotContain(section.Descendants().SelectMany(e => e.Attributes()), a => a.Value.Contains("AncestorType=ScrollViewer", StringComparison.Ordinal));
            // An action's fields are the picked step's own, made inside it,
            // so nothing in the section takes the selected action as its
            // data or asks whether there is one.
            Assert.DoesNotContain(section.Descendants(), e => (string)e.Attribute("DataContext") == "{Binding SelectedAction}");
            Assert.DoesNotContain(section.Descendants().SelectMany(e => e.Attributes()), a => a.Value.Contains("DataContext.SelectedAction", StringComparison.Ordinal));
            // A keyboard menu's Opens With row takes its opener's row as its
            // data inside a border that shows by the menu's own style, so it
            // asks no other element, and the editor with no menu picked has no
            // lookup that finds nothing.
            var openRow = Assert.Single(section.Descendants(), e => (string)e.Attribute("DataContext") == "{Binding OpenRow}");
            Assert.Null(openRow.Attribute("Visibility"));
            Assert.Equal("Border", openRow.Parent!.Name.LocalName);
            Assert.Equal("{Binding IsKeyboard, Converter={StaticResource BoolToVisibilityConverter}}", (string)openRow.Parent.Attribute("Visibility"));
            Assert.DoesNotContain(section.Descendants().SelectMany(e => e.Attributes()), a => a.Value.Contains("ElementName=MenuEditorHost", StringComparison.Ordinal));

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
            Assert.Equal("{Binding Macro_When_Header" + S, (string)whenRows[0].Attribute("Text"));
            var inputs = whenRows[1];
            Assert.Equal(combination, (string)inputs.Attribute("Visibility"));
            string inputsText = inputs.ToString();
            foreach (string part in new[] { "SlotMacroTriggerChoicesView", "RecordTriggerCommand", "Macro_RecordHint", "RecordingLiveText", "TriggerInputItems" })
                Assert.Contains(part, inputsText);
            // Clear Trigger is an item of the macro's More.
            Assert.DoesNotContain("ClearTriggerCommand", inputsText);
            Assert.Contains("{Binding TriggerMode, Mode=TwoWay}", whenRows[2].ToString());
            Assert.Null(whenRows[2].Attribute("Visibility"));
            Assert.Equal(new[] { "ShowsHoldTimeRow", "IsDoublePressMode", "ShowsInlineIntervalRow", "IsCustomExpressionMode" },
                whenRows.Skip(3).Take(4).Select(Gate));
            Assert.Equal("FormulaEditor", Name(whenRows[6]));
            Assert.Contains("Macro_AlwaysDesc", whenRows[7].ToString());

            // Do: the head, the actions, and Add Action. The picked action's
            // fields stand in its own step.
            var doRows = Assert.Single(act.Elements()).Elements().ToList();
            Assert.Equal(3, doRows.Count);
            Assert.Equal("{Binding Macro_Do_Header" + S, (string)doRows[0].Attribute("Text"));
            Assert.Equal("ActionsList", Name(doRows[1]));
            Assert.Equal("{Binding AddActionCommand}", (string)doRows[2].Attribute("Command"));

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

        /// <summary>The menu editor's rows stand in blocks, as its board
        /// draws them: Opens holds the menu's style and what opens and
        /// steers it, Cells a cell menu's cells, Keyboard a keyboard menu's
        /// own rows, and More, a fold at the card's foot, what a menu
        /// seldom needs. Every row kept the rule that shows it. No row
        /// shares a label column with another: a row's label is the
        /// section's 190. No row has a caption under it: what a caption
        /// said is its label's tooltip.</summary>
        [Fact]
        public void TheMenuEditorsRowsStandInOpens_Cells_Keyboard_AndMore()
        {
            var page = Page();
            var host = Named(page, "MenuEditorHost");
            Assert.Null(host.Attribute("Grid.IsSharedSizeScope"));
            var opens = Named(host, "MenuOpensBlock");
            var cells = Named(host, "MenuCellsBlock");
            var keyboard = Named(host, "MenuKeyboardBlock");
            var more = Named(host, "MenuMore");
            Assert.Equal(new[] { "DockPanel", "Border", "Border", "Border", "MoreRow" }, host.Elements().Select(e => e.Name.LocalName));
            Assert.Equal(new[] { opens, cells, keyboard, more }, host.Elements().Skip(1));
            Assert.All(new[] { opens, cells, keyboard }, b => Assert.Equal("{StaticResource EditorBlock}", (string)b.Attribute("Style")));
            // A block's head has the blocks' look.
            Assert.All(new[] { opens, cells, keyboard }, b => Assert.Equal("{StaticResource EditorBlockHead}",
                (string)Assert.Single(b.Elements()).Elements().First().Attribute("Style")));
            // And the board's words: Opens, Cells, and Keyboard, the name of
            // the style a keyboard menu is.
            Assert.Equal(new[] { "{Binding Menu_Opens_Header" + S, "{Binding Menu_Cells" + S, "{Binding Menu_Keyboard_Card" + S },
                new[] { opens, cells, keyboard }.Select(b => (string)Assert.Single(b.Elements()).Elements().First().Attribute("Text")));

            string Gate(XElement row) => ((string)row.Attribute("Visibility") ?? "{Binding ").Substring("{Binding ".Length).Split(',')[0];
            // What a row is of: the string of its own text or of its first
            // part's, or the list it shows.
            string What(XElement row)
            {
                string bound = (string)row.Attribute("Text") ?? (string)row.Attribute("ItemsSource")
                    ?? (string)row.Elements().FirstOrDefault()?.Attribute("Text") ?? "";
                return bound.StartsWith("{Binding ", StringComparison.Ordinal) ? bound.Substring("{Binding ".Length).Split(',', '}')[0] : "";
            }
            const string cellMenu = "{Binding IsCellMenu, Converter={StaticResource BoolToVisibilityConverter}}";
            const string keys = "{Binding IsKeyboard, Converter={StaticResource BoolToVisibilityConverter}}";
            Assert.Null(opens.Attribute("Visibility"));
            Assert.Equal(cellMenu, (string)cells.Attribute("Visibility"));
            Assert.Equal(keys, (string)keyboard.Attribute("Visibility"));

            // Opens: the style, then each style's own rows under its rule.
            var opensRows = Assert.Single(opens.Elements()).Elements().Skip(1).ToList();
            Assert.Equal(new[]
                {
                    "", "IsKeyboard", "IsKeyboard", "IsKeyboard", "IsKeyboard", "IsKeyboard",
                    "IsCellMenu", "ShowsCustomAxesRows", "ShowsCustomAxesRows",
                    "IsHotbar", "IsHotbar", "IsHotbar", "IsHotbar", "IsHotbar", "IsHotbar", "IsHotbar", "ShowsHostHalfRow",
                },
                opensRows.Select(Gate));
            Assert.Contains("{Binding KindIndex, Mode=TwoWay}", opensRows[0].ToString());
            Assert.Equal("{Binding OpenRow}", (string)Assert.Single(opensRows[3].Elements()).Attribute("DataContext"));

            // Cells: the list of them, then Add Cell. The block carries the
            // rule. A ring's center cell is a row under More.
            Assert.Equal("{Binding Menu_Cells" + S, (string)Assert.Single(cells.Elements()).Elements().First().Attribute("Text"));
            var cellRows = Assert.Single(cells.Elements()).Elements().Skip(1).ToList();
            Assert.Equal(new[] { "ListBox", "Button" }, cellRows.Select(r => r.Name.LocalName));
            Assert.Equal("{Binding Cells}", (string)cellRows[0].Attribute("ItemsSource"));
            Assert.All(cellRows, r => Assert.Null(r.Attribute("Visibility")));

            // Keyboard: its own rows, then Keyboard Controls under a small
            // head. The block carries the rule, and none of them repeats it.
            var keyboardRows = Assert.Single(keyboard.Elements()).Elements().Skip(1).ToList();
            Assert.Equal(new[]
                {
                    "Menu_TypesThrough", "Menu_FunctionRow", "Menu_WordSuggestions", "Menu_SymbolsPage",
                    "Menu_Keyboard_Controls", "Menu_Keyboard_Controls_Desc", "Menu_Deadzone", "KeyboardControlRows",
                },
                keyboardRows.Select(What));
            Assert.All(keyboardRows, r => Assert.Null(r.Attribute("Visibility")));

            // More: the rest, each under its own rule, in the board's order.
            Assert.Equal("Pad.Menus.More", (string)more.Attribute("StateKey"));
            Assert.Equal("0", (string)more.Attribute("Margin"));
            var moreRows = Assert.Single(more.Elements(), e => e.Name.LocalName == "StackPanel").Elements().ToList();
            Assert.Equal(new[]
                {
                    "", "Menu_LayerHoldsOpen", "Menu_ShowOnWebController", "Menu_ClickInput", "Menu_FireMode", "SelectedFireDescription",
                    "Menu_HasCenter", "Menu_Deadzone", "Dashboard_MenuOverlay", "Menu_ShowLabels",
                    "ResetLabel_PositionX", "ResetLabel_PositionY", "Menu_Scale", "Menu_Opacity",
                },
                moreRows.Select(What));
            Assert.Equal(new[]
                {
                    "", "ShowLayerHold", "ShowWebControllerRow", "ShowsClickRow", "IsCellMenu", "IsCellMenu",
                    "IsRadial", "IsCellMenu", "", "IsCellMenu",
                    "", "", "", "",
                },
                moreRows.Select(Gate));
            // Icon Packages is a line of its own under the editor's card.
            Assert.DoesNotContain(more.Descendants(), e => Name(e) == "IconPackagesFoldCard");

            // What each caption said is the tooltip of its row's label, after
            // what that tooltip said before, and no caption is a line of its
            // own.
            string[] captions =
            {
                "Menu_Style_Caption", "Menu_Keyboard_OpensWith_Caption", "Menu_OpenOnTextFocus_Caption", "HostInputCaption",
                "Menu_Hotbar_Caption", "Menu_TypesThrough_Caption", "Menu_FunctionRow_Caption", "Menu_WordSuggestions_Caption",
                "Menu_Keyboard_Overlay_Caption",
            };
            foreach (string caption in captions)
            {
                var uses = host.Descendants().SelectMany(e => e.Attributes())
                    .Where(a => a.Value.StartsWith("{Binding " + caption + ",", StringComparison.Ordinal) || a.Value == "{Binding " + caption + "}")
                    .ToList();
                Assert.NotEmpty(uses);
                Assert.All(uses, a => Assert.True(a.Name.LocalName == "ToolTip"
                    || a.Parent!.Ancestors().Any(p => p.Name.LocalName == "TextBlock.ToolTip"), caption + " is a line of its own"));
            }
            foreach (var (label, tip, caption) in new[]
            {
                ("{Binding Menu_Style" + S, "{Binding Menu_Style_Tip" + S, "{Binding Menu_Style_Caption" + S),
                ("{Binding HostInputLabel}", "{Binding HostInputTip}", "{Binding HostInputCaption}"),
                ("{Binding Menu_Hotbar_Show" + S, "{Binding Menu_Hotbar_Show_Tip" + S, "{Binding Menu_Hotbar_Caption" + S),
            })
            {
                var owner = Assert.Single(host.Descendants(), e => e.Name.LocalName == "TextBlock"
                    && (string)e.Attribute("Style") == "{DynamicResource RowOptionLabel}" && (string)e.Attribute("Text") == label);
                var paragraphs = Assert.Single(owner.Elements(), e => e.Name.LocalName == "TextBlock.ToolTip")
                    .Descendants().Where(e => e.Name.LocalName == "TextBlock").ToList();
                Assert.Equal(new[] { tip, caption }, paragraphs.Select(p => (string)p.Attribute("Text")));
                Assert.All(paragraphs, p => Assert.Equal("Wrap", (string)p.Attribute("TextWrapping")));
            }
            // The overlay's caption spoke of a keyboard menu alone, and its
            // tooltip shows on one.
            var overlay = Assert.Single(host.Descendants(), e => (string)e.Attribute("Text") == "{Binding Dashboard_MenuOverlay" + S);
            Assert.Equal("{Binding Menu_Keyboard_Overlay_Caption" + S, (string)overlay.Attribute("ToolTip"));
            Assert.Equal("{Binding IsKeyboard}", (string)overlay.Attribute("ToolTipService.IsEnabled"));
        }

        /// <summary>The menu editor's numbers are the page's number boxes, as
        /// the Macros board draws them: a field 64 by 34 with its unit after
        /// it, or where a slider stands beside the number the section's
        /// slider with its value and the percent sign. No spin box is
        /// left.</summary>
        [Fact]
        public void TheMenuEditorsNumbersAreThePagesNumberBoxes()
        {
            var page = Page();
            var host = Named(page, "MenuEditorHost");
            Assert.DoesNotContain(host.Descendants(), e => e.Name.LocalName == "NumberBox");
            var fields = host.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource CardNumberBox}").ToList();
            Assert.Equal(new[]
                {
                    "{Binding OpenPressMs, Mode=TwoWay, UpdateSourceTrigger=LostFocus}",
                    "{Binding PosXPercent, Mode=TwoWay, UpdateSourceTrigger=LostFocus}",
                    "{Binding PosYPercent, Mode=TwoWay, UpdateSourceTrigger=LostFocus}",
                    "{Binding ScalePercent, Mode=TwoWay, UpdateSourceTrigger=LostFocus}",
                    "{Binding OpacityPercent, Mode=TwoWay, UpdateSourceTrigger=LostFocus}",
                },
                fields.Select(f => (string)f.Attribute("Text")));
            // Each field has a name, and its unit stands after it.
            Assert.All(fields, f => Assert.StartsWith("{Binding ", (string)f.Attribute("AutomationProperties.Name")));
            var units = fields.Select(f => f.ElementsAfterSelf().First()).ToList();
            Assert.Equal(new[] { "{Binding Common_Ms" + S, "%", "%", "%", "%" }, units.Select(u => (string)u.Attribute("Text")));
            Assert.All(units, u => Assert.Equal("{StaticResource DzPercent}", (string)u.Attribute("Style")));
            var field = Keyed(page, "CardNumberBox");
            Assert.Equal("64", SetterOf(field, "Width"));
            Assert.Equal("34", SetterOf(field, "MinHeight"));
            Assert.Equal("Right", SetterOf(field, "TextAlignment"));

            // Engage Deadzone, in Keyboard Controls and under More: the
            // section's slider, its value and the percent sign.
            var sliders = host.Descendants().Where(e => e.Name.LocalName == "Slider"
                && (string)e.Attribute("Value") == "{Binding EngageDeadzonePercent, Mode=TwoWay}").ToList();
            Assert.Equal(2, sliders.Count);
            Assert.All(sliders, s =>
            {
                Assert.Equal("{DynamicResource RowOptionDeadzoneSlider}", (string)s.Attribute("Style"));
                Assert.Null(s.Attribute("Width"));
                Assert.Equal("{Binding Menu_Deadzone" + S, (string)s.Attribute("AutomationProperties.Name"));
                var value = s.ElementsAfterSelf().First();
                Assert.Equal("TextBox", value.Name.LocalName);
                Assert.Equal("{StaticResource DzValueEdit}", (string)value.Attribute("Style"));
                Assert.Equal("{Binding EngageDeadzonePercent, Mode=TwoWay, UpdateSourceTrigger=LostFocus}", (string)value.Attribute("Text"));
                Assert.Equal("{Binding Menu_Deadzone" + S, (string)value.Attribute("AutomationProperties.Name"));
                var unit = value.ElementsAfterSelf().First();
                Assert.Equal("%", (string)unit.Attribute("Text"));
                Assert.Equal("{StaticResource DzPercent}", (string)unit.Attribute("Style"));
            });
        }

        /// <summary>Icon Packages is a line of a card of its own under the
        /// editor's card, the Sound Packages line's shape, while a menu with
        /// cells shows (#574). The packages' buttons and Keyboard Controls'
        /// Map All are the page's buttons, with no padding of their
        /// own.</summary>
        [Fact]
        public void IconPackagesIsALineOfItsOwnUnderTheMenuEditor()
        {
            var page = Page();
            var column = Named(page, "MacrosEditorColumn");
            var block = Named(page, "IconPackagesBlock");
            Assert.Equal(new[] { "MacrosEditorCard", "IconPackagesBlock" }, column.Elements().Select(Name));
            Assert.Equal("0,16,0,0", (string)block.Attribute("Margin"));
            // It shows on the menus' part while the picked menu has cells.
            var look = Assert.Single(Assert.Single(block.Elements(), e => e.Name.LocalName == "Border.Style").Elements());
            Assert.Equal("Collapsed", SetterOf(look, "Visibility"));
            var shows = Assert.Single(look.Descendants(), e => e.Name.LocalName == "MultiDataTrigger");
            Assert.Equal(new[] { "{Binding MacrosPartIndex}=1", "{Binding SelectedMenu.IsCellMenu}=True" },
                shows.Descendants().Where(e => e.Name.LocalName == "Condition")
                    .Select(c => (string)c.Attribute("Binding") + "=" + (string)c.Attribute("Value")));
            Assert.Equal("Visible", (string)Assert.Single(shows.Elements(), e => e.Name.LocalName == "Setter").Attribute("Value"));
            var list = Assert.Single(block.Elements(), e => e.Name.LocalName == "FoldList");
            var card = Assert.Single(list.Elements());
            Assert.Equal("IconPackagesFoldCard", Name(card));
            Assert.Null(card.Attribute("Visibility"));
            Assert.DoesNotContain(Named(page, "MenuMore").Descendants(), e => Name(e) == "IconPackagesFoldCard");

            // The packages' buttons and Map All take the page's button look.
            var buttons = page.Descendants().Where(e => (string)e.Attribute("Click") is "IconPackageAdd_Click" or "IconPackageCreate_Click"
                or "IconPackageRemove_Click" or "PackageAdd_Click" or "PackageCreate_Click" or "PackageRemove_Click").ToList();
            Assert.Equal(6, buttons.Count);
            buttons.Add(Assert.Single(page.Descendants(), e => (string)e.Attribute("Content") == "{Binding MapAllText}"));
            Assert.All(buttons, b =>
            {
                Assert.Equal("Button", b.Name.LocalName);
                Assert.Null(b.Attribute("Padding"));
                Assert.Null(b.Attribute("Style"));
            });
        }

        /// <summary>A row of a setting in the menu editor is the section's
        /// row, as a macro's is: its label, its control and its reset at
        /// the row's end. A check box is a switch under its own label. A
        /// group inside a block stands under a small head with its reset,
        /// in no card, and no row shares a label column with another. A
        /// row whose control has a line under it is laid out from its top,
        /// with its label and its reset on the picker's line.</summary>
        [Fact]
        public void AMenuRowIsItsLabel_ItsControl_AndItsResetAtTheEnd()
        {
            var page = Page();
            var host = Named(page, "MenuEditorHost");
            bool IsRow(XElement e) => e.Name.LocalName == "FlexLinePanel" && (string)e.Attribute("Style") == "{DynamicResource RowOptionLine}";
            bool IsReset(XElement e) => e.Name.LocalName == "SettingResetButton" || (string)e.Attribute("Style") == "{StaticResource ResetButton}";
            // A setter of a style of the page's own resources.
            string PageStyleSetter(string style, string property)
            {
                string key = style.Substring("{StaticResource ".Length).TrimEnd('}');
                var resources = Assert.Single(page.Root!.Elements(), e => e.Name.LocalName == "UserControl.Resources");
                return SetterOf(Assert.Single(resources.Elements(), e => (string)e.Attribute(X + "Key") == key), property);
            }
            var rows = host.Descendants().Where(IsRow).ToList();
            // 33 settings of the menu and the four fields of a cell.
            Assert.Equal(37, rows.Count);
            Assert.Equal(4, rows.Count(r => r.Ancestors().Any(a => Name(a) == "CellsList")));
            // A keyboard control's row is a template beside the editor.
            var control = Assert.Single(Keyed(page, "KeyboardControlRowTemplate").Elements());
            Assert.True(IsRow(control));
            rows.Add(control);

            // No card is left in the editor, and no shared column of the
            // menus on the page.
            Assert.DoesNotContain(host.Descendants(), e => (string)e.Attribute("Style") == "{StaticResource CardBorder}");
            Assert.DoesNotContain(page.Descendants(), e => ((string)e.Attribute("SharedSizeGroup") ?? "").StartsWith("Menu", StringComparison.Ordinal));
            Assert.DoesNotContain(host.DescendantsAndSelf(), e => e.Attribute("Grid.IsSharedSizeScope") != null);

            Assert.All(rows, r =>
            {
                var parts = r.Elements().ToList();
                Assert.Equal("TextBlock", parts[0].Name.LocalName);
                Assert.Equal("{DynamicResource RowOptionLabel}", (string)parts[0].Attribute("Style"));
                Assert.StartsWith("{Binding ", (string)parts[0].Attribute("Text"));
                // One reset, the row's last part, marked as its end part.
                Assert.True(IsReset(parts[^1]), "a row ends with " + parts[^1].Name.LocalName);
                Assert.Equal(1, parts.Count(IsReset));
                Assert.Equal("True", (string)parts[^1].Attributes().First(a => a.Name.LocalName == "FlexLinePanel.AtEnd"));
                // A picker or a box of the row stands centered in its height,
                // by its own setting or by its style's.
                Assert.All(parts.Where(c => c.Name.LocalName is "ComboBox" or "TextBox" or "NumberBox"),
                    c => Assert.Equal("Center", (string)c.Attribute("VerticalAlignment") ?? PageStyleSetter((string)c.Attribute("Style"), "VerticalAlignment")));
            });

            // A check box of the rows is a switch, which says what the
            // row's label says.
            var blocks = new[] { "MenuOpensBlock", "MenuCellsBlock", "MenuKeyboardBlock", "MenuMore" }.Select(n => Named(host, n));
            Assert.All(blocks.SelectMany(b => b.Descendants()).Where(e => e.Name.LocalName == "CheckBox"), box => Assert.True(IsRow(box.Parent!)));
            var switches = rows.Where(r => r.Elements().Any(c => c.Name.LocalName == "CheckBox")).ToList();
            Assert.Equal(new[]
                {
                    "{Binding OpenOnTextFocus, Mode=TwoWay}", "{Binding HotbarWrap, Mode=TwoWay}", "{Binding HotbarDismissOnFire, Mode=TwoWay}",
                    "{Binding HotbarRecenter, Mode=TwoWay}", "{Binding FunctionRow, Mode=TwoWay}", "{Binding Suggestions, Mode=TwoWay}",
                    "{Binding LayerHoldsOpen, Mode=TwoWay}", "{Binding ShowOnWebController, Mode=TwoWay}", "{Binding HasCenter}", "{Binding ShowLabels}",
                },
                switches.Select(r => (string)r.Elements().Single(c => c.Name.LocalName == "CheckBox").Attribute("IsChecked")));
            Assert.All(switches, r =>
            {
                var box = r.Elements().Single(c => c.Name.LocalName == "CheckBox");
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)box.Attribute("Style"));
                Assert.Equal((string)r.Elements().First().Attribute("Text"), (string)box.Attribute("Content"));
            });

            // Center Cell, under More: a ring's row, with a reset of its own.
            var center = switches.Single(r => r.Elements().Any(c => (string)c.Attribute("IsChecked") == "{Binding HasCenter}"));
            Assert.Contains(Named(host, "MenuMore").Descendants(), e => e == center);
            Assert.Equal("{Binding IsRadial, Converter={StaticResource BoolToVisibilityConverter}}", (string)center.Attribute("Visibility"));
            Assert.Equal("HasCenter", (string)center.Elements().Last().Attribute("CommandParameter"));
            Assert.Equal("{Binding ResetSettingCommand}", (string)center.Elements().Last().Attribute("Command"));

            // A small head: 12 SemiBold in the quiet text color, on a line
            // with what belongs to its group and the group's reset at the
            // line's end.
            var head = Keyed(page, "EditorSubHead");
            Assert.Equal("12", SetterOf(head, "FontSize"));
            Assert.Equal("SemiBold", SetterOf(head, "FontWeight"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", SetterOf(head, "Foreground"));
            var heads = host.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource EditorSubHead}").ToList();
            Assert.Equal(new[] { "{Binding Menu_Keyboard_Controls" + S, "{Binding Dashboard_MenuOverlay" + S }, heads.Select(h => (string)h.Attribute("Text")));
            Assert.Equal(new[] { "{Binding ResetKeyboardControlsCommand}", "{Binding ResetGeometryCommand}" }, heads.Select(h =>
            {
                var line = h.Parent!;
                Assert.Equal("FlexLinePanel", line.Name.LocalName);
                Assert.Same(h, line.Elements().First());
                Assert.Equal("True", (string)line.Attribute("EndsApart"));
                Assert.Equal("44", (string)line.Attribute("MinLineHeight"));
                var reset = line.Elements().Last();
                Assert.Equal("True", (string)reset.Attributes().First(a => a.Name.LocalName == "FlexLinePanel.AtEnd"));
                return (string)reset.Attribute("Command");
            }));
            // Keyboard Controls keeps its count and Map All on the line.
            var controlsLine = heads[0].Parent!.Elements().ToList();
            Assert.Equal("{Binding KeyboardControlsSummary}", (string)controlsLine[1].Attribute("Text"));
            Assert.Equal("{Binding MapAllText}", (string)controlsLine[2].Attribute("Content"));

            // Where the overlay sits is a row for each of its two numbers,
            // named as its reset is: a number box and its unit.
            foreach (string axis in new[] { "X", "Y" })
            {
                var row = rows.Single(r => (string)r.Elements().First().Attribute("Text") == "{Binding ResetLabel_Position" + axis + S);
                Assert.Equal(new[] { "TextBlock", "TextBox", "TextBlock", "SettingResetButton" }, row.Elements().Select(e => e.Name.LocalName));
                Assert.Equal("{Binding Pos" + axis + "Percent, Mode=TwoWay, UpdateSourceTrigger=LostFocus}", (string)row.Elements().ElementAt(1).Attribute("Text"));
                Assert.Equal("%", (string)row.Elements().ElementAt(2).Attribute("Text"));
                Assert.Equal("Pos" + axis + "Percent", (string)row.Elements().Last().Attribute("CommandParameter"));
                Assert.Equal("{Binding ResetLabel_Position" + axis + S, (string)row.Elements().Last().Attribute("SettingLabel"));
            }

            // The layer row shows by the holder around it, as a macro's does.
            var layer = rows.Single(r => r.Elements().Any(c => (string)c.Attribute("SelectedValue") == "{Binding LayerMask, Mode=TwoWay}"));
            Assert.Null(layer.Attribute("Visibility"));
            Assert.Equal("StackPanel", layer.Parent!.Name.LocalName);
            Assert.Equal(new[] { "{Binding DataContext.HasShiftLayers, RelativeSource={RelativeSource AncestorType=UserControl}}", "{Binding ShowsLayerRow}" },
                layer.Parent.Elements().Single(e => e.Name.LocalName == "StackPanel.Style").Descendants()
                    .Where(e => e.Name.LocalName == "DataTrigger").Select(e => (string)e.Attribute("Binding")));

            // A row laid out from its top: 4 over and under it, as a row of
            // one line has around its control. Its label and its reset stand
            // on the picker's line, and the picker and its Record are one
            // line of the control's stack, so what stands under the picker
            // moves neither.
            var fromTop = rows.Where(r => r.Elements().Any(c => c.Name.LocalName == "StackPanel" && (string)c.Attribute("Orientation") == "Vertical")).ToList();
            Assert.Equal(9, fromTop.Count);
            Assert.Contains(control, fromTop);
            Assert.All(fromTop, r =>
            {
                Assert.Equal("0,4,0,4", (string)r.Attribute("Margin"));
                var parts = r.Elements().ToList();
                Assert.Equal(new[] { "TextBlock", "StackPanel", "Button" }, parts.Select(p => p.Name.LocalName));
                Assert.Equal("Top", (string)parts[0].Attribute("VerticalAlignment"));
                Assert.Equal("0,8,12,0", (string)parts[0].Attribute("Margin"));
                Assert.Equal("Top", (string)parts[2].Attribute("VerticalAlignment"));
                Assert.Equal("10,2,0,0", (string)parts[2].Attribute("Margin"));
                var line = parts[1].Elements().First();
                Assert.Equal("StackPanel", line.Name.LocalName);
                Assert.Equal("Horizontal", (string)line.Attribute("Orientation"));
                Assert.Equal(new[] { "ComboBox", "Button" }, line.Elements().Select(p => p.Name.LocalName));
                Assert.Equal("36", (string)line.Elements().First().Attribute("MinHeight"));
                Assert.Equal("{StaticResource RecordButton}", (string)line.Elements().Last().Attribute("Style"));
                Assert.Null(line.Elements().Last().Attribute("VerticalAlignment"));
            });
            // Every other row's reset has the rows' margin.
            Assert.All(rows.Except(fromTop), r => Assert.Equal("{DynamicResource RowOptionResetMargin}", (string)r.Elements().Last().Attribute("Margin")));
        }

        /// <summary>A record button, as the board draws it: the ember dot and
        /// the word Record, 36 tall beside a picker of the same height. Its
        /// content is the glyph that says whether it records, and its face
        /// follows that: a square and Stop while it records. Every record
        /// button of the section has the look and a name of its own, since
        /// the glyph reads as nothing.</summary>
        [Fact]
        public void ARecordButtonIsTheBoardsDotAndWord()
        {
            var page = Page();
            var look = Keyed(page, "RecordButton");
            Assert.Equal("ui:Button", (string)look.Attribute("TargetType"));
            Assert.Equal("{StaticResource DefaultUiButtonStyle}", (string)look.Attribute("BasedOn"));
            Assert.Equal("{StaticResource PadUiButtonTemplate}", SetterOf(look, "Template"));
            Assert.Equal("{StaticResource RecordFace}", SetterOf(look, "ContentTemplate"));
            Assert.Equal("36", SetterOf(look, "MinHeight"));
            Assert.Equal("12,0", SetterOf(look, "Padding"));
            Assert.Equal("10,0,0,0", SetterOf(look, "Margin"));
            Assert.Equal("Center", SetterOf(look, "VerticalAlignment"));

            var face = Keyed(page, "RecordFace");
            var parts = Assert.Single(face.Elements(), e => e.Name.LocalName == "StackPanel").Elements().ToList();
            Assert.Equal(new[] { "Rectangle", "TextBlock" }, parts.Select(e => e.Name.LocalName));
            Assert.Equal(new[] { "7", "7", "3.5", "3.5" }, new[] { "Width", "Height", "RadiusX", "RadiusY" }.Select(a => (string)parts[0].Attribute(a)));
            Assert.Equal("{DynamicResource EmberBrush}", (string)parts[0].Attribute("Fill"));
            Assert.Equal("0,0,8,0", (string)parts[0].Attribute("Margin"));
            Assert.Equal("{Binding Common_Record" + S, (string)parts[1].Attribute("Text"));

            // The face, made and shown: the dot and Record on the record
            // glyph, a square and Stop on the stop glyph.
            var made = new XElement(face);
            made.Attribute(X + "Key")!.Remove();
            made.SetAttributeValue(XNamespace.Xmlns + "x", X.NamespaceName);
            made.SetAttributeValue(XNamespace.Xmlns + "strings", "clr-namespace:PadForge.Resources.Strings;assembly=" + typeof(PadPage).Assembly.GetName().Name);
            string xaml = made.ToString();
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var template = (System.Windows.DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
                    var shown = new ContentPresenter { Content = MenuEditorItem.RecordGlyph, ContentTemplate = template };
                    void Settle()
                    {
                        shown.Measure(new System.Windows.Size(300, 60));
                        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    }
                    Settle();
                    var word = (TextBlock)template.FindName("Word", shown);
                    var mark = (System.Windows.Shapes.Rectangle)template.FindName("Mark", shown);
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Common_Record, word.Text);
                    Assert.Equal(3.5, mark.RadiusX);
                    shown.Content = MenuEditorItem.StopGlyph;
                    Settle();
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Common_Stop, word.Text);
                    Assert.Equal(1.0, mark.RadiusX);
                    Assert.Equal(1.0, mark.RadiusY);
                    shown.Content = MenuEditorItem.RecordGlyph;
                    Settle();
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Common_Record, word.Text);
                    Assert.Equal(3.5, mark.RadiusX);
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();

            // The glyphs a face reads are the ones the view models show.
            Assert.Equal("\uE7C8", MenuEditorItem.RecordGlyph);
            Assert.Equal("\uE71A", MenuEditorItem.StopGlyph);
            var macro = new MacroItem();
            Assert.Equal(MenuEditorItem.RecordGlyph, macro.RecordTriggerIcon);
            macro.IsRecordingTrigger = true;
            Assert.Equal(MenuEditorItem.StopGlyph, macro.RecordTriggerIcon);

            // Every record button of the section.
            var section = Named(page, "MacrosSectionScroll");
            var records = section.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource RecordButton}").ToList();
            Assert.Equal(16, records.Count);
            Assert.All(records, r =>
            {
                Assert.Matches(@"^\{Binding \w*Record\w*Icon\}$", (string)r.Attribute("Content"));
                Assert.StartsWith("{Binding ", (string)r.Attribute("AutomationProperties.Name"));
                Assert.StartsWith("{Binding ", (string)r.Attribute("Command"));
                // Nothing is drawn in it but its face. A tooltip may hang on it.
                Assert.All(r.Elements(), e => Assert.Equal("Button.ToolTip", e.Name.LocalName));
            });
            // None is left that draws the glyph itself.
            Assert.DoesNotContain(section.Descendants(), e => e.Name.LocalName == "GlyphText"
                && System.Text.RegularExpressions.Regex.IsMatch((string)e.Attribute("Text") ?? "", @"^\{Binding \w*Record\w*Icon\}$"));

            // A picker beside a Record is as tall as it. Any other picker of
            // the section is the board's 34.
            var looks = Assert.Single(section.Elements(), e => e.Name.LocalName == "ScrollViewer.Resources");
            var pickerLook = Assert.Single(looks.Elements(), e => e.Name.LocalName == "Style" && (string)e.Attribute("TargetType") == "ComboBox");
            Assert.Null(pickerLook.Attribute(X + "Key"));
            Assert.Equal("{StaticResource {x:Type ComboBox}}", (string)pickerLook.Attribute("BasedOn"));
            Assert.Equal("34", SetterOf(pickerLook, "MinHeight"));
            var beside = records.Select(r => r.ElementsBeforeSelf().LastOrDefault())
                .Where(e => e != null && e.Name.LocalName == "ComboBox").ToList();
            Assert.Equal(14, beside.Count);
            Assert.All(beside, p => Assert.Equal("36", (string)p.Attribute("MinHeight")));
        }

        /// <summary>A ring's center cell is a setting of its own under More,
        /// with a reset of its own, which leaves the ring's cells.</summary>
        [Fact]
        public void TheCenterCellHasAResetOfItsOwn()
        {
            var menu = new MenuEditorItem(new PadForge.Engine.Menus.MenuDefinitionEntry
            {
                MenuId = 1, Kind = PadForge.Engine.Menus.MenuKind.Radial, CellCount = 6, HasCenter = true,
            });
            Assert.Equal(7, menu.Cells.Count);
            Assert.True(MenuEditorItem.CanResetSetting(nameof(MenuEditorItem.HasCenter)));
            var raised = new List<string>();
            menu.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            menu.ResetSettingCommand.Execute(nameof(MenuEditorItem.HasCenter));
            Assert.False(menu.HasCenter);
            Assert.False(menu.Entry.HasCenter);
            Assert.Equal(6, menu.CellCount);
            Assert.Contains(nameof(MenuEditorItem.HasCenter), raised);
            // The ring's cells are left, and the center's is gone.
            Assert.Equal(6, menu.Cells.Count);
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
            // deadzone, and the point an input with a depth fires at. And
            // the rows of a step's fields, which stand in the steps' list.
            var ofSteps = rows.Where(r => r.Ancestors().Any(a => Name(a) == "ActionsList")).ToList();
            Assert.Equal(106, ofSteps.Count);
            Assert.Equal(17, rows.Count - ofSteps.Count);
            // A step's type is the first of its rows, and a check box of the
            // fields became a switch under its own label.
            Assert.Contains(ofSteps, r => r.Elements().Any(c => (string)c.Attribute("SelectedValue") == "{Binding Type, Mode=TwoWay}"));
            var switches = ofSteps.Where(r => r.Elements().Any(c => c.Name.LocalName == "CheckBox")).ToList();
            Assert.Equal(11, switches.Count);
            Assert.All(switches, r =>
            {
                var box = r.Elements().Single(c => c.Name.LocalName == "CheckBox");
                Assert.Equal("{StaticResource SwitchCheckBox}", (string)box.Attribute("Style"));
                Assert.StartsWith("{Binding ", (string)box.Attribute("IsChecked"));
            });
            // A group of fields stands in the step itself: none is a card
            // inside the step, and none has a title that repeats the
            // action's type, which the Type row and the step's head say.
            var list = Named(page, "ActionsList");
            Assert.DoesNotContain(list.Descendants(), e => (string)e.Attribute("Style") == "{StaticResource CardBorder}");
            Assert.DoesNotContain(list.Descendants(), e => e.Name.LocalName == "TextBlock" && (string)e.Attribute("FontWeight") == "SemiBold"
                && ((string)e.Attribute("Text") ?? "").StartsWith("{Binding MacroAction_Type_", StringComparison.Ordinal));
            Assert.DoesNotContain("EditorTitle", page.ToString());
            // No row of the old shape is left among the fields: a strip with
            // a label 80, 100 or 120 wide before one control and one reset.
            Assert.DoesNotContain(list.Descendants(), e => e.Name.LocalName == "StackPanel" && (string)e.Attribute("Orientation") == "Horizontal"
                && e.Elements().FirstOrDefault() is XElement first && first.Name.LocalName == "TextBlock"
                && (string)first.Attribute("Width") is "80" or "100" or "120"
                && e.Elements().Count(c => c.Name.LocalName == "SettingResetButton" || (string)c.Attribute("Style") == "{StaticResource ResetButton}") == 1);
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
            // A parameter of a trigger effect has no reset, and its row none.
            var parameter = Assert.Single(ofSteps, r => (string)r.Elements().First().Attribute("Text") == "{Binding Label}");
            Assert.Equal(new[] { "TextBlock", "Slider", "TextBox" }, parameter.Elements().Select(c => c.Name.LocalName));
            Assert.All(rows.Where(r => r != parameter), r =>
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

        /// <summary>A macro's actions are numbered steps, as the board draws
        /// them: one under the other, each 42 tall on the band fill with
        /// its number, what it does and its remove. The picked step is
        /// open: it has Duplicate and shows its fields under its head. The
        /// list keeps its selection and the handlers that move a step.
        /// Under the steps stands Add Action, with no fill and a plus,
        /// drawn out so the plus is at the steps' edge.</summary>
        [Fact]
        public void AMacrosActionsAreNumberedSteps()
        {
            var page = Page();
            var act = Named(page, "MacroDoBlock");
            var list = Named(act, "ActionsList");
            Assert.Equal("ListBox", list.Name.LocalName);
            // The list stands in the block itself, in no well, and has no
            // height or scroll view of its own.
            Assert.Same(Assert.Single(act.Elements()), list.Parent);
            Assert.Equal("{StaticResource PartList}", (string)list.Attribute("Style"));
            Assert.Null(list.Attribute("MinHeight"));
            Assert.Null(list.Attribute("MaxHeight"));
            Assert.Equal("{Binding Actions}", (string)list.Attribute("ItemsSource"));
            Assert.Equal("{Binding SelectedAction, Mode=TwoWay}", (string)list.Attribute("SelectedItem"));
            Assert.Equal("True", (string)list.Attribute("AllowDrop"));
            foreach (string handler in new[] { "PreviewKeyDown", "PreviewMouseLeftButtonDown", "PreviewMouseMove", "DragOver", "Drop" })
                Assert.Equal("ActionsList_" + handler, (string)list.Attribute(handler));

            // A step's head: its number from the list's count of it, what
            // it does, then Duplicate and its remove at the end.
            Assert.Equal("{StaticResource StepItem}", (string)list.Attribute("ItemContainerStyle"));
            Assert.Equal("100000", (string)list.Attribute("AlternationCount"));
            var headTemplate = Assert.Single(list.Elements(), e => e.Name.LocalName == "ListBox.ItemTemplate").Elements().Single();
            var row = headTemplate.Elements().First();
            Assert.Equal("Grid", row.Name.LocalName);
            var parts = row.Elements().Where(e => e.Name.LocalName != "Grid.ColumnDefinitions").ToList();
            Assert.Equal(new[] { "TextBlock", "TextBlock", "Button", "Button" }, parts.Select(e => e.Name.LocalName));
            Assert.Equal(new[] { null, "1", "2", "3" }, parts.Select(e => (string)e.Attribute("Grid.Column")));
            Assert.Equal("{Binding (ItemsControl.AlternationIndex), RelativeSource={RelativeSource AncestorType=ListBoxItem}, Converter={StaticResource OneBasedIndexConverter}}",
                (string)parts[0].Attribute("Text"));
            Assert.Equal("18", (string)parts[0].Attribute("Width"));
            Assert.Equal("0,0,10,0", (string)parts[0].Attribute("Margin"));
            Assert.Equal("{StaticResource TelemetryFontFamily}", (string)parts[0].Attribute("FontFamily"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)parts[0].Attribute("Foreground"));
            Assert.Equal("{Binding DisplayText}", (string)parts[1].Attribute("Text"));
            Assert.Equal("Wrap", (string)parts[1].Attribute("TextWrapping"));
            Assert.All(parts.Skip(2), b => Assert.Equal("{StaticResource StepButton}", (string)b.Attribute("Style")));
            // Duplicate copies the picked step, so only the open step has it.
            Assert.Equal("Duplicate", Name(parts[2]));
            Assert.Equal("Collapsed", (string)parts[2].Attribute("Visibility"));
            Assert.Equal("{Binding DataContext.DuplicateActionCommand, RelativeSource={RelativeSource AncestorType=ListBox}}", (string)parts[2].Attribute("Command"));
            Assert.Equal("{Binding Common_Duplicate" + S, (string)parts[2].Attribute("AutomationProperties.Name"));
            Assert.Equal("Remove", Name(parts[3]));
            Assert.Null(parts[3].Attribute("Visibility"));
            Assert.Equal("{Binding DataContext.RemoveStepCommand, RelativeSource={RelativeSource AncestorType=ListBox}}", (string)parts[3].Attribute("Command"));
            Assert.Equal("{Binding}", (string)parts[3].Attribute("CommandParameter"));
            Assert.Equal("{Binding Common_Remove" + S, (string)parts[3].Attribute("AutomationProperties.Name"));
            var open = Assert.Single(headTemplate.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}", (string)open.Attribute("Binding"));
            Assert.Equal(new[] { "Duplicate.Visibility=Visible", "Duplicate.IsTabStop=True", "Remove.IsTabStop=True" },
                open.Elements().Select(s => (string)s.Attribute("TargetName") + "." + (string)s.Attribute("Property") + "=" + (string)s.Attribute("Value")));

            // The picked step's fields: a template of the list, which a step
            // shows under its head while it is picked. The list reaches them
            // with Tab and searches no step by a typed letter.
            Assert.Equal("Continue", (string)list.Attribute("KeyboardNavigation.TabNavigation"));
            Assert.Equal("False", (string)list.Attribute("IsTextSearchEnabled"));
            var templates = Assert.Single(list.Elements(), e => e.Name.LocalName == "ListBox.Resources").Elements().ToList();
            Assert.All(templates, t => Assert.Equal("DataTemplate", t.Name.LocalName));
            var fields = templates[^1];
            Assert.Equal("StepFields", (string)fields.Attribute(X + "Key"));
            var fieldsRoot = Assert.Single(fields.Elements());
            Assert.Equal("StepFields_KeyDown", (string)fieldsRoot.Attribute("KeyDown"));
            // The action's type is the first field, and the old head over
            // the fields is gone: the step's own head is theirs.
            Assert.Contains("{Binding Type, Mode=TwoWay}", fieldsRoot.Elements().First().ToString());
            Assert.DoesNotContain("Macro_EditAction", page.ToString());

            // A group of fields that shows by a flag of the action is a
            // template of the list's own, and the fields hold it in a part
            // that is built when it first shows. A picked step builds the
            // fields of its own type and no others: built whole, the fields
            // took about 200 ms each time a step was picked.
            var groups = fieldsRoot.Elements().Where(e => e.Name.LocalName == "ShownPart").ToList();
            Assert.Equal(52, groups.Count);
            var groupTemplates = templates.Take(templates.Count - 1).ToList();
            var keys = groupTemplates.Select(t => (string)t.Attribute(X + "Key")).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.All(keys, k => Assert.StartsWith("StepFields", k));
            Assert.Equal(keys.Select(k => "{StaticResource " + k + "}"), groups.Select(g => (string)g.Attribute("PartTemplate")));
            Assert.All(groups, g => Assert.Matches(@"^\{Binding (Is|Shows)\w+\}$", (string)g.Attribute("Show")));
            // The show rule is the part's. A group's template has none at
            // its root, and no group is left in the fields that would show
            // by a binding of its own: a presenter hidden that way is built
            // all the same.
            Assert.All(groupTemplates, t => Assert.Null(Assert.Single(t.Elements()).Attribute("Visibility")));
            Assert.DoesNotContain(fieldsRoot.Elements(), e => e.Name.LocalName is "StackPanel" or "Border" or "ContentPresenter"
                && ((string)e.Attribute("Visibility") ?? "").StartsWith("{Binding ", StringComparison.Ordinal));

            var step = Keyed(page, "StepItem");
            Assert.Equal("{Binding DisplayText, TargetNullValue=''}", SetterOf(step, "AutomationProperties.Name"));
            Assert.Equal("42", SetterOf(step, "MinHeight"));
            Assert.Equal("12,0,4,0", SetterOf(step, "Padding"));
            Assert.Equal("0,0,0,6", SetterOf(step, "Margin"));
            var body = Named(step, "Bd");
            Assert.Equal("6", (string)body.Attribute("CornerRadius"));
            Assert.Equal("1", (string)body.Attribute("BorderThickness"));
            Assert.Equal("{DynamicResource SteelBandBrush}", (string)body.Attribute("Background"));
            Assert.Equal("{DynamicResource SteelLineSoftBrush}", (string)body.Attribute("BorderBrush"));
            // Inside the edge: the head, 40 tall, and under it the place of
            // the fields, which holds nothing until the step is picked.
            var inside = Assert.Single(body.Elements()).Elements().ToList();
            Assert.Equal(new[] { "Head", "Fields" }, inside.Select(Name));
            Assert.Equal("40", (string)inside[0].Attribute("MinHeight"));
            Assert.Equal("{TemplateBinding Padding}", (string)inside[0].Attribute("Padding"));
            Assert.Equal("ContentPresenter", inside[1].Name.LocalName);
            Assert.Equal("{Binding}", (string)inside[1].Attribute("Content"));
            Assert.Equal("Collapsed", (string)inside[1].Attribute("Visibility"));
            Assert.Null(inside[1].Attribute("ContentTemplate"));
            // The picked step: an ember rim, the ember fill on its head, and
            // its fields.
            var picked = Assert.Single(step.Descendants(), e => e.Name.LocalName == "Trigger" && (string)e.Attribute("Property") == "IsSelected");
            Assert.Equal(new[]
                {
                    "Bd.BorderBrush=#66FF6B2C", "Head.Background={DynamicResource EmberCardPickBrush}", "Head.CornerRadius=5,5,0,0",
                    "Fields.ContentTemplate={DynamicResource StepFields}", "Fields.Visibility=Visible",
                },
                picked.Elements().Select(s => (string)s.Attribute("TargetName") + "." + (string)s.Attribute("Property") + "=" + (string)s.Attribute("Value")));
            var buttonLook = Keyed(page, "StepButton");
            Assert.Equal("{StaticResource QuietResetButton}", (string)buttonLook.Attribute("BasedOn"));
            Assert.Equal("False", SetterOf(buttonLook, "IsTabStop"));

            // Under the steps: Add Action alone, as on the board.
            var add = Assert.Single(list.ElementsAfterSelf());
            Assert.Equal("{Binding AddActionCommand}", (string)add.Attribute("Command"));
            Assert.Equal("{StaticResource QuietTextButton}", (string)add.Attribute("Style"));
            Assert.Null(add.Attribute("Padding"));
            Assert.Null(add.Attribute("Foreground"));
            Assert.Equal("-14,2,0,0", (string)add.Attribute("Margin"));
            Assert.Equal("Left", (string)add.Attribute("HorizontalAlignment"));
            Assert.Equal("{Binding Macro_AddAction" + S, (string)add.Attribute("AutomationProperties.Name"));
            Assert.Equal(new[] { "Path", "TextBlock" }, Assert.Single(add.Elements()).Elements().Select(e => e.Name.LocalName));
            Assert.Equal("{Binding Macro_AddAction" + S, (string)add.Descendants().Single(e => e.Name.LocalName == "TextBlock").Attribute("Text"));

            // The old chips are gone from the steps' heads.
            Assert.DoesNotContain(headTemplate.Descendants(), e => (string)e.Attribute("Style") == "{StaticResource ChipEmber}");
        }

        /// <summary>A step's own remove takes that step out. The selected
        /// action stays selected unless it is the one removed, and then the
        /// last one is, as Remove leaves it.</summary>
        [Fact]
        public void AStepsOwnRemoveTakesThatStepOut_AndKeepsThePick()
        {
            var macro = new MacroItem();
            var first = new MacroAction { Type = MacroActionType.Delay, DurationMs = 10 };
            var second = new MacroAction { Type = MacroActionType.Delay, DurationMs = 20 };
            var third = new MacroAction { Type = MacroActionType.Delay, DurationMs = 30 };
            foreach (var action in new[] { first, second, third }) macro.Actions.Add(action);
            macro.SelectedAction = third;

            // Another step than the picked one: the pick stays.
            macro.RemoveStepCommand.Execute(first);
            Assert.Equal(new[] { second, third }, macro.Actions);
            Assert.Same(third, macro.SelectedAction);

            // The picked one: the last is picked.
            macro.RemoveStepCommand.Execute(third);
            Assert.Equal(new[] { second }, macro.Actions);
            Assert.Same(second, macro.SelectedAction);

            // One that is not in the list, and none: nothing changes.
            macro.RemoveStepCommand.Execute(first);
            macro.RemoveStepCommand.Execute(null);
            Assert.Equal(new[] { second }, macro.Actions);
            Assert.Same(second, macro.SelectedAction);

            // The last step: none is left, and none is picked.
            macro.RemoveStepCommand.Execute(second);
            Assert.Empty(macro.Actions);
            Assert.Null(macro.SelectedAction);
        }

        /// <summary>A drag takes hold of a step by its head. A press on one
        /// of the step's buttons or among the open step's fields starts no
        /// drag: a hand that moves as it presses the remove, drags a slider
        /// or selects text in a box would carry the step off. The keys that
        /// move a step act only with the focus on the step itself, and an
        /// arrow no field took does not go on to move the list's pick.</summary>
        [Fact]
        public void ADragTakesAStepByItsHead_AndAFieldsKeysAreItsOwn()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var glyph = new System.Windows.Shapes.Path();
                    var remove = new Button { Content = glyph };
                    var words = new TextBlock();
                    var box = new TextBox();
                    var row = new DockPanel();
                    row.Children.Add(remove);
                    row.Children.Add(words);
                    row.Children.Add(new ContentPresenter { Name = "Fields", Content = box });
                    var step = new ListBoxItem { Content = row };
                    // Measured, so the step's parts stand in its visual tree,
                    // as the parts of a step on the page do.
                    step.Measure(new System.Windows.Size(300, 100));
                    Assert.True(PadPage.IsOffAStepsHead(glyph));
                    Assert.True(PadPage.IsOffAStepsHead(remove));
                    Assert.True(PadPage.IsOffAStepsHead(box));
                    Assert.False(PadPage.IsOffAStepsHead(words));
                    Assert.False(PadPage.IsOffAStepsHead(row));
                    Assert.False(PadPage.IsOffAStepsHead(step));
                    Assert.False(PadPage.IsOffAStepsHead(null));
                    // A button the step itself stands in is not the step's.
                    var inner = new TextBlock();
                    var around = new Button { Content = new ListBoxItem { Content = inner } };
                    around.Measure(new System.Windows.Size(300, 100));
                    Assert.Same(around, System.Windows.Media.VisualTreeHelper.GetParent(
                        System.Windows.Media.VisualTreeHelper.GetParent(System.Windows.Media.VisualTreeHelper.GetParent((ListBoxItem)around.Content))));
                    Assert.False(PadPage.IsOffAStepsHead(inner));
                }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();

            Assert.Matches(@"private void ActionsList_PreviewMouseLeftButtonDown\(object sender, MouseButtonEventArgs e\)\s*\{\s*_actionDragStart = e\.GetPosition\(null\);"
                + @"[^}]*var pressed = e\.OriginalSource as DependencyObject;\s*_actionDragItem = IsOffAStepsHead\(pressed\) \? null : ActionFromVisual\(pressed\);\s*\}", Code());

            // The keys that move a step: only with the focus on the step.
            Assert.Matches(@"private void ActionsList_PreviewKeyDown\(object sender, KeyEventArgs e\)\s*\{\s*if \(sender is not ListBox lb\) return;"
                + @"[^}]*if \(e\.OriginalSource is not ListBoxItem\) return;\s*int step = ReorderStep\(", Code());
            // The keys the list moves its pick by stop at the fields' panel.
            foreach (var key in new[] { System.Windows.Input.Key.Up, System.Windows.Input.Key.Down, System.Windows.Input.Key.PageUp,
                         System.Windows.Input.Key.PageDown, System.Windows.Input.Key.Home, System.Windows.Input.Key.End })
                Assert.True(PadPage.MovesTheListsPick(key), key.ToString());
            foreach (var key in new[] { System.Windows.Input.Key.Left, System.Windows.Input.Key.Right, System.Windows.Input.Key.Tab,
                         System.Windows.Input.Key.Enter, System.Windows.Input.Key.Space, System.Windows.Input.Key.Delete, System.Windows.Input.Key.A })
                Assert.False(PadPage.MovesTheListsPick(key), key.ToString());
            Assert.Matches(@"private void StepFields_KeyDown\(object sender, KeyEventArgs e\)\s*\{\s*if \(MovesTheListsPick\(e\.Key\)\) e\.Handled = true;\s*\}", Code());
        }

        /// <summary>The inputs of a macro's trigger stand on one line, as
        /// the board draws them: a pill for each with its remove, "and"
        /// between two, then Record and Add from List. Clear Trigger is an
        /// item of the macro's More. The line wraps, and its own parts go
        /// on from the last pill. Under it stand the record hint, what is
        /// being recorded, and the settings of each input that has any, as
        /// rows.</summary>
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
            // no input, Record and Add from List.
            var own = line.Elements().ToList();
            Assert.Equal(new[] { "TextBlock", "Button", "Grid" }, own.Select(e => e.Name.LocalName));
            Assert.Equal("{Binding Macro_NotSet" + S, (string)own[0].Attribute("Text"));
            Assert.Equal("{Binding HasTriggerInputItems, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}",
                (string)own[0].Attribute("Visibility"));
            Assert.All(own.Skip(1), part => Assert.Null(part.Attribute("Visibility")));

            // Record: the section's record button, 30 tall on this line and
            // with no margin of its own, since the line spaces its parts. Its
            // content is the glyph that says whether it records, and its
            // name says what it records.
            var record = own[1];
            Assert.Equal("{StaticResource RecordButton}", (string)record.Attribute("Style"));
            Assert.Equal("{Binding RecordTriggerIcon}", (string)record.Attribute("Content"));
            Assert.Equal("{Binding RecordTriggerCommand}", (string)record.Attribute("Command"));
            Assert.Equal("30", (string)record.Attribute("MinHeight"));
            Assert.Equal("0", (string)record.Attribute("Margin"));
            Assert.Equal("{Binding RecordTriggerButtonText, TargetNullValue=''}", (string)record.Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding Macro_RecordHint" + S, (string)record.Attribute("ToolTip"));
            Assert.Empty(record.Elements());

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

            Assert.DoesNotContain(when.Descendants(), e => (string)e.Attribute("Command") == "{Binding ClearTriggerCommand}");

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

        /// <summary>A menu's cells are numbered rows, as the board draws
        /// them: one under the other in the step's look, each with its
        /// number, its name, what it does in the quiet color and an edit
        /// pencil. The picked cell is open on its fields, which are rows of
        /// the section with a reset each. Under the list stands Add Cell,
        /// drawn out so its plus is at the cells' edge. Remove Last Cell is
        /// an item of the menu's More.</summary>
        [Fact]
        public void AMenusCellsAreNumberedRows()
        {
            var page = Page();
            var block = Named(page, "MenuCellsBlock");
            var list = Named(block, "CellsList");
            Assert.Equal("ListBox", list.Name.LocalName);
            Assert.Equal("{Binding Cells}", (string)list.Attribute("ItemsSource"));
            Assert.Equal("{Binding SelectedCell, Mode=TwoWay}", (string)list.Attribute("SelectedItem"));
            Assert.Equal("{StaticResource PartList}", (string)list.Attribute("Style"));
            Assert.Equal("{StaticResource CellItem}", (string)list.Attribute("ItemContainerStyle"));
            Assert.Equal("Continue", (string)list.Attribute("KeyboardNavigation.TabNavigation"));
            Assert.Equal("False", (string)list.Attribute("IsTextSearchEnabled"));

            // A cell has the step's look and is read by its name.
            var look = Keyed(page, "CellItem");
            Assert.Equal("{StaticResource StepItem}", (string)look.Attribute("BasedOn"));
            Assert.Equal("{Binding RowName, TargetNullValue=''}", SetterOf(look, "AutomationProperties.Name"));

            string[] Setters(XElement trigger) => trigger.Elements()
                .Select(s => (string)s.Attribute("TargetName") + "." + (string)s.Attribute("Property") + "=" + (string)s.Attribute("Value")).ToArray();

            // Its head: the number, the name, what it does, then the reset of
            // the whole cell, which the open cell shows, and the pencil.
            var headTemplate = Assert.Single(Assert.Single(list.Elements(), e => e.Name.LocalName == "ListBox.ItemTemplate").Elements());
            var parts = Assert.Single(headTemplate.Elements(), e => e.Name.LocalName == "Grid").Elements()
                .Where(e => e.Name.LocalName != "Grid.ColumnDefinitions").ToList();
            Assert.Equal(new[] { "TextBlock", "TextBlock", "TextBlock", "Button", "Button" }, parts.Select(e => e.Name.LocalName));
            Assert.Equal("{Binding Index}", (string)parts[0].Attribute("Text"));
            Assert.Equal("18", (string)parts[0].Attribute("Width"));
            Assert.Equal("0,0,10,0", (string)parts[0].Attribute("Margin"));
            Assert.Equal("13", (string)parts[0].Attribute("FontSize"));
            Assert.Equal("{StaticResource TelemetryFontFamily}", (string)parts[0].Attribute("FontFamily"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)parts[0].Attribute("Foreground"));
            Assert.Equal("Words", Name(parts[1]));
            Assert.Equal("{Binding RowName}", (string)parts[1].Attribute("Text"));
            Assert.Null(parts[1].Attribute("Foreground"));
            Assert.Equal("{Binding Summary}", (string)parts[2].Attribute("Text"));
            Assert.Equal("13", (string)parts[2].Attribute("FontSize"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)parts[2].Attribute("Foreground"));
            Assert.Equal("ResetAll", Name(parts[3]));
            Assert.Equal("Collapsed", (string)parts[3].Attribute("Visibility"));
            Assert.Equal("{Binding ResetCellCommand}", (string)parts[3].Attribute("Command"));
            Assert.Equal("{Binding Menu_Reset_Cell" + S, (string)parts[3].Attribute("AutomationProperties.Name"));
            Assert.Equal("Edit", Name(parts[4]));
            Assert.Null(parts[4].Attribute("Visibility"));
            Assert.Equal("{Binding DataContext.EditCellCommand, RelativeSource={RelativeSource AncestorType=ListBox}}", (string)parts[4].Attribute("Command"));
            Assert.Equal("{Binding}", (string)parts[4].Attribute("CommandParameter"));
            Assert.Equal("{Binding Menu_EditCell" + S, (string)parts[4].Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding Menu_EditCell" + S, (string)parts[4].Attribute("ToolTip"));
            Assert.All(parts.Skip(3), b => Assert.Equal("{StaticResource StepButton}", (string)b.Attribute("Style")));
            var triggers = headTemplate.Descendants().Where(e => e.Name.LocalName == "DataTrigger").ToList();
            // The name is in the quiet color only while it is the cell's place.
            Assert.Equal(new[] { "{Binding IsPlaceName}", "{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" },
                triggers.Select(t => (string)t.Attribute("Binding")));
            Assert.Equal("True", (string)triggers[0].Attribute("Value"));
            Assert.Equal(new[] { "Words.Foreground={DynamicResource TextFillColorTertiaryBrush}" }, Setters(triggers[0]));
            Assert.Equal(new[] { "ResetAll.Visibility=Visible", "ResetAll.IsTabStop=True", "Edit.IsTabStop=True" }, Setters(triggers[1]));

            // The open cell's fields: the template the step's look opens, of
            // this list's own. Four rows of the section: the name, what the
            // cell does, its icon, and the icon's size while it has one.
            var fields = Assert.Single(Assert.Single(list.Elements(), e => e.Name.LocalName == "ListBox.Resources").Elements());
            Assert.Equal("StepFields", (string)fields.Attribute(X + "Key"));
            var fieldsRoot = Assert.Single(fields.Elements());
            Assert.Equal("StepFields_KeyDown", (string)fieldsRoot.Attribute("KeyDown"));
            var rows = fieldsRoot.Elements().ToList();
            Assert.All(rows, r => Assert.Equal("{DynamicResource RowOptionLine}", (string)r.Attribute("Style")));
            Assert.Equal(new[] { "Label", "BindingKind", "IconName", "IconScalePercent" },
                rows.Select(r => (string)r.Elements().Last().Attribute("CommandParameter")));
            // A row's label names it for the cell, and its reset says the same.
            Assert.Equal(new[] { "{Binding Menu_Cell_Name" + S, "{Binding Menu_Cell_Action" + S, "{Binding Menu_Cell_Icon" + S, "{Binding Menu_IconScale" + S },
                rows.Select(r => (string)r.Elements().First().Attribute("Text")));
            Assert.Equal(new[] { "{Binding Menu_Cell_Name" + S, "{Binding Menu_Cell_Action" + S, "{Binding Menu_Cell_Icon" + S },
                rows.Take(3).Select(r => (string)r.Elements().Last().Attribute("SettingLabel")));
            Assert.All(rows, r => Assert.Equal("{Binding ResetSettingCommand}", (string)r.Elements().Last().Attribute("Command")));
            Assert.Equal(new[] { null, null, null, "{Binding HasIcon, Converter={StaticResource BoolToVisibilityConverter}}" },
                rows.Select(r => (string)r.Attribute("Visibility")));
            Assert.Contains(rows[0].Elements(), e => (string)e.Attribute("Text") == "{Binding Label, Mode=TwoWay, UpdateSourceTrigger=LostFocus}");
            var pickers = rows[1].Elements().Where(e => e.Name.LocalName == "ComboBox").ToList();
            Assert.Equal(new[]
                {
                    "{Binding BindingKind, Mode=TwoWay}", "{Binding SelectedKeyVk, Mode=TwoWay}",
                    "{Binding SelectedButtonFlag, Mode=TwoWay}", "{Binding SelectedMacroName, Mode=TwoWay}",
                },
                pickers.Select(e => (string)e.Attribute("SelectedValue")));
            Assert.Equal(new[] { "", "ShowKeyPicker", "ShowButtonPicker", "ShowMacroPicker" },
                pickers.Select(e => ((string)e.Attribute("Visibility") ?? "{Binding ").Substring("{Binding ".Length).Split(',')[0]));
            // Each picker of a target has a name of its own: the row's label
            // names the kind's.
            Assert.All(pickers.Skip(1), e => Assert.StartsWith("{Binding Menu_Binding_", (string)e.Attribute("AutomationProperties.Name")));
            Assert.Contains(rows[2].Elements(), e => (string)e.Attribute("Click") == "MenuCellChooseIcon_Click");
            Assert.Contains(rows[2].Elements(), e => (string)e.Attribute("Text") == "{Binding Menu_Icon_None" + S
                && (string)e.Attribute("Visibility") == "{Binding HasIcon, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}");
            Assert.Contains(rows[3].Elements(), e => (string)e.Attribute("Value") == "{Binding IconScalePercent, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}");

            // Under the list: Add Cell alone, quiet in the text color, at the
            // line's start.
            var add = Assert.Single(list.ElementsAfterSelf());
            Assert.Equal("Button", add.Name.LocalName);
            Assert.Equal("-14,2,0,0", (string)add.Attribute("Margin"));
            Assert.Equal("Left", (string)add.Attribute("HorizontalAlignment"));
            Assert.Equal("MenuAddCell", (string)add.Attribute("AutomationProperties.AutomationId"));
            Assert.Equal("{Binding AddCellCommand}", (string)add.Attribute("Command"));
            Assert.Equal("{Binding Menu_AddCell" + S, (string)add.Attribute("AutomationProperties.Name"));
            Assert.Equal("{Binding Menu_AddCell" + S, (string)add.Descendants().Single(e => e.Name.LocalName == "TextBlock").Attribute("Text"));
            Assert.Equal("{StaticResource QuietTextButton}", (string)add.Attribute("Style"));
            Assert.Equal(new[] { "Path", "TextBlock" }, Assert.Single(add.Elements()).Elements().Select(e => e.Name.LocalName));
            // The mark is drawn in the button's own color, which dims.
            Assert.Equal("{Binding Foreground, RelativeSource={RelativeSource AncestorType={x:Type ui:Button}}}",
                (string)add.Descendants().Single(e => e.Name.LocalName == "Path").Attribute("Stroke"));
            Assert.DoesNotContain(block.Descendants(), e => (string)e.Attribute("Command") == "{Binding RemoveLastCellCommand}");
            var quiet = Keyed(page, "QuietTextButton");
            Assert.Equal("{StaticResource QuietButton}", (string)quiet.Attribute("BasedOn"));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", SetterOf(quiet, "Foreground"));
            Assert.Equal("14,0", SetterOf(quiet, "Padding"));
            var off = Assert.Single(quiet.Descendants(), e => e.Name.LocalName == "Trigger");
            Assert.Equal("IsEnabled", (string)off.Attribute("Property"));
            Assert.Equal("False", (string)off.Attribute("Value"));
            Assert.Equal("{DynamicResource TextFillColorDisabledBrush}", (string)Assert.Single(off.Elements()).Attribute("Value"));

            // The count box and the pickers on every cell are gone.
            Assert.DoesNotContain(block.Descendants(), e => ((string)e.Attribute("Value") ?? "").StartsWith("{Binding CellCount", StringComparison.Ordinal));
            Assert.DoesNotContain(block.Descendants(), e => e.Name.LocalName == "ItemsControl");
            Assert.DoesNotContain("ResetCellsCommand", page.ToString());
        }

        /// <summary>Cells come and go at the end. Add Cell adds one and
        /// opens it, up to the most a menu has. Remove takes the last away
        /// and leaves at least one. No cell changes its number, and what
        /// the last cell held comes back with it. A cell stays open across
        /// a rebuild of the rows while the shape has its number.</summary>
        [Fact]
        public void CellsComeAndGoAtTheEnd()
        {
            var menu = new MenuEditorItem(new PadForge.Engine.Menus.MenuDefinitionEntry
            {
                MenuId = 1, Kind = PadForge.Engine.Menus.MenuKind.Radial, CellCount = 3,
            });
            Assert.Null(menu.SelectedCell);
            menu.Cells[0].Label = "First";
            menu.Cells[2].Label = "Third";

            int addChanges = 0, removeChanges = 0;
            menu.AddCellCommand.CanExecuteChanged += (_, _) => addChanges++;
            menu.RemoveLastCellCommand.CanExecuteChanged += (_, _) => removeChanges++;

            menu.AddCellCommand.Execute(null);
            Assert.Equal(4, menu.CellCount);
            Assert.Equal(new[] { 1, 2, 3, 4 }, menu.Cells.Select(c => c.Index));
            Assert.Equal(new[] { "First", "", "Third", "" }, menu.Cells.Select(c => c.Label));
            Assert.Same(menu.Cells[3], menu.SelectedCell);
            Assert.Equal(1, addChanges);
            Assert.Equal(1, removeChanges);

            // The pencil opens a cell and closes the open one.
            menu.EditCellCommand.Execute(menu.Cells[0]);
            Assert.Same(menu.Cells[0], menu.SelectedCell);
            menu.EditCellCommand.Execute(menu.Cells[0]);
            Assert.Null(menu.SelectedCell);
            menu.EditCellCommand.Execute(menu.Cells[2]);
            Assert.Equal(3, menu.SelectedCell.Index);

            // Remove takes the last cell. The open one stays open while the
            // shape has its number, on the row made anew for it.
            menu.RemoveLastCellCommand.Execute(null);
            Assert.Equal(3, menu.CellCount);
            Assert.Same(menu.Cells[2], menu.SelectedCell);
            Assert.Equal("Third", menu.SelectedCell.Label);
            menu.RemoveLastCellCommand.Execute(null);
            Assert.Equal(new[] { "First", "" }, menu.Cells.Select(c => c.Label));
            Assert.Null(menu.SelectedCell);
            // What the third cell held comes back with it.
            menu.AddCellCommand.Execute(null);
            Assert.Equal("Third", menu.Cells[2].Label);
            Assert.Same(menu.Cells[2], menu.SelectedCell);

            // A ring's center is a cell of its own, and the open ring cell
            // stays the open one when it comes.
            menu.EditCellCommand.Execute(menu.Cells[0]);
            menu.HasCenter = true;
            Assert.True(menu.Cells[0].IsCenter);
            Assert.Same(menu.Cells[1], menu.SelectedCell);
            Assert.Equal("First", menu.SelectedCell.Label);
            menu.HasCenter = false;

            // At least one cell, and at most the menu's limit.
            menu.RemoveLastCellCommand.Execute(null);
            menu.RemoveLastCellCommand.Execute(null);
            Assert.Equal(1, menu.CellCount);
            Assert.False(menu.RemoveLastCellCommand.CanExecute(null));
            menu.RemoveLastCellCommand.Execute(null);
            Assert.Equal(1, menu.CellCount);
            Assert.Equal(20, MenuEditorItem.MaxCells);
            while (menu.AddCellCommand.CanExecute(null)) menu.AddCellCommand.Execute(null);
            Assert.Equal(MenuEditorItem.MaxCells, menu.CellCount);
            menu.AddCellCommand.Execute(null);
            Assert.Equal(MenuEditorItem.MaxCells, menu.CellCount);
            Assert.True(menu.RemoveLastCellCommand.CanExecute(null));
        }

        /// <summary>A cell's row says its name and what it does: its label,
        /// else what it runs, else its place, and the kind of its binding
        /// with the key or the macro of that kind. Each of its fields has a
        /// reset, which leaves the others.</summary>
        [Fact]
        public void ACellsRowSaysItsNameAndWhatItDoes()
        {
            var words = PadForge.Resources.Strings.Strings.Instance;
            var menu = new MenuEditorItem(new PadForge.Engine.Menus.MenuDefinitionEntry
            {
                MenuId = 1, Kind = PadForge.Engine.Menus.MenuKind.Radial, CellCount = 3, HasCenter = true,
            });
            menu.MacroNamesProvider = () => new[] { "Quick Combo" };
            var center = menu.Cells[0];
            var cell = menu.Cells[1];
            Assert.True(center.IsCenter);
            Assert.Equal(words.Menu_CellCenter, center.RowName);
            Assert.False(cell.HasLabel);
            Assert.Equal(string.Format(words.Menu_CellIndex_Format, 1), cell.RowName);
            Assert.Equal(words.Menu_Binding_None, cell.Summary);

            var raised = new List<string>();
            cell.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            cell.Label = "Reload";
            Assert.True(cell.HasLabel);
            Assert.Equal("Reload", cell.RowName);
            Assert.Contains(nameof(MenuCellItem.RowName), raised);
            Assert.Contains(nameof(MenuCellItem.HasLabel), raised);

            raised.Clear();
            cell.BindingKind = 1;
            Assert.Contains(nameof(MenuCellItem.Summary), raised);
            raised.Clear();
            cell.SelectedKeyVk = 0x52;
            Assert.Contains(nameof(MenuCellItem.Summary), raised);
            string key = cell.KeyOptions.First(k => k.Vk == 0x52).Label;
            Assert.Equal(words.Menu_Binding_Key + ", " + key, cell.Summary);

            raised.Clear();
            cell.BindingKind = MenuCellItem.MacroKind;
            Assert.Contains(nameof(MenuCellItem.Summary), raised);
            Assert.Equal(words.Menu_Binding_Macro + ", Quick Combo", cell.Summary);

            // Each field's reset.
            Assert.All(new[] { nameof(MenuCellItem.Label), nameof(MenuCellItem.BindingKind), nameof(MenuCellItem.IconName), nameof(MenuCellItem.IconScalePercent) },
                name => Assert.True(MenuCellItem.CanResetSetting(name), name));
            cell.ResetSettingCommand.Execute(nameof(MenuCellItem.BindingKind));
            Assert.Equal(0, cell.BindingKind);
            Assert.Equal(words.Menu_Binding_None, cell.Summary);
            Assert.Equal("Reload", cell.Label);
            cell.SetIcon("\U0001F3AF");
            Assert.True(cell.HasIcon);
            cell.IconScalePercent = 150;
            Assert.Equal(150, cell.IconScalePercent);
            cell.ResetSettingCommand.Execute(nameof(MenuCellItem.IconScalePercent));
            Assert.Equal(100, cell.IconScalePercent);
            Assert.True(cell.HasIcon);
            cell.IconScalePercent = 150;
            cell.ResetSettingCommand.Execute(nameof(MenuCellItem.IconName));
            Assert.False(cell.HasIcon);
            Assert.Equal(100, cell.IconScalePercent);
            Assert.Equal("Reload", cell.Label);
            cell.ResetSettingCommand.Execute(nameof(MenuCellItem.Label));
            Assert.Equal("", cell.Label);
            Assert.False(cell.HasLabel);
            // With no label and nothing it runs, the cell is named by its
            // place, which the list draws in the quiet color.
            Assert.Equal(string.Format(words.Menu_CellIndex_Format, 1), cell.RowName);
            Assert.True(cell.IsPlaceName);
            Assert.True(center.IsPlaceName);
            // A cell with no label is named by what it runs, the macro or the
            // key, and says so whenever that changes.
            raised.Clear();
            cell.BindingKind = MenuCellItem.MacroKind;
            Assert.Equal("Quick Combo", cell.RowName);
            Assert.False(cell.IsPlaceName);
            Assert.Contains(nameof(MenuCellItem.RowName), raised);
            Assert.Contains(nameof(MenuCellItem.IsPlaceName), raised);
            raised.Clear();
            cell.BindingKind = 1;
            cell.SelectedKeyVk = 0x52;
            Assert.Equal(key, cell.RowName);
            Assert.False(cell.IsPlaceName);
            Assert.Contains(nameof(MenuCellItem.RowName), raised);
            Assert.Contains(nameof(MenuCellItem.IsPlaceName), raised);
            // Its own label comes first.
            cell.Label = "Reload";
            Assert.Equal("Reload", cell.RowName);
            Assert.False(cell.IsPlaceName);
            cell.ResetSettingCommand.Execute(nameof(MenuCellItem.Label));
            Assert.Equal(key, cell.RowName);
            raised.Clear();
            cell.ResetSettingCommand.Execute(nameof(MenuCellItem.BindingKind));
            Assert.Equal(string.Format(words.Menu_CellIndex_Format, 1), cell.RowName);
            Assert.True(cell.IsPlaceName);
            Assert.Contains(nameof(MenuCellItem.RowName), raised);
            Assert.Contains(nameof(MenuCellItem.IsPlaceName), raised);
            // A cell with nothing left is no entry of the menu.
            Assert.DoesNotContain(menu.Entry.Items ?? new List<PadForge.Engine.Menus.MenuItemDefinition>(), i => i.Index == 1);
        }
    }
}
