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
    }
}
