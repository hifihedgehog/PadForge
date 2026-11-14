using System;
using System.Linq;
using System.Xml.Linq;
using PadForge.Services;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Profiles page (#574), held to its board of the world-class pass:
    /// the head with Get Profiles and New Profile, a card for each profile
    /// with its game line, its type chips and its foot, the active card lit,
    /// the drop tile after the cards, four starter profiles, and the
    /// Automatic Switching, Shortcuts and External Control cards. These hold
    /// the parts in their order, every AutomationId a UI tool finds a control
    /// by, and every reset a setting keeps.
    /// </summary>
    public class ProfilesPageTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string S = ", Source={x:Static strings:Strings.Instance}}";

        private static string Text() => ExtrasPageTests.RepoText("PadForge.App", "Views", "ProfilesPage.xaml");

        private static XDocument Page() => XDocument.Parse(Text());

        private static XElement Named(XContainer scope, string name)
            => Assert.Single(scope.Descendants(), e => (string)e.Attribute(X + "Name") == name);

        private static XElement ById(XContainer scope, string id)
            => Assert.Single(scope.Descendants(), e => (string)e.Attribute("AutomationProperties.AutomationId") == id);

        private static XElement Card(XDocument page)
            => Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == "ProfileCard");

        /// <summary>An attached property's value, whatever prefix names its owner.</summary>
        private static string Attached(XElement e, string property)
            => (string)e.Attributes().SingleOrDefault(a => a.Name.LocalName == property);

        [Fact]
        public void TheHead_IsTitleSubtitleGetProfilesAndNewProfile()
        {
            var page = Page();
            var stack = Assert.Single(page.Root.Element(page.Root.Name.Namespace + "ScrollViewer").Elements());
            // The boards' gutters: 36 at the sides and the foot, 26 over the title.
            Assert.Equal("36,26,36,36", (string)stack.Attribute("Margin"));

            var head = Named(page, "ProfilesHead");
            Assert.Equal("FlexLinePanel", head.Name.LocalName);
            var parts = head.Elements().ToList();
            Assert.Equal(2, parts.Count);

            var title = Named(parts[0], "ProfilesTitle");
            Assert.Equal("{Binding Profiles_Title" + S, (string)title.Attribute("Text"));
            Assert.Equal("28", (string)title.Attribute("FontSize"));
            Assert.Equal("{StaticResource DisplayFontFamily}", (string)title.Attribute("FontFamily"));
            Assert.Equal("SemiBold", (string)title.Attribute("FontWeight"));
            Assert.Equal("Level1", (string)title.Attribute("AutomationProperties.HeadingLevel"));
            Assert.Contains(parts[0].Elements(), e => (string)e.Attribute("Text") == "{Binding Profiles_Subtitle" + S);
            // The title's block gives way down to 240 before the buttons
            // leave its line: at the window's least width the page is 564
            // wide inside its gutters, and the two buttons take about 260.
            Assert.Equal("240", Attached(parts[0], "FlexLinePanel.Basis"));
            Assert.Equal("1", Attached(parts[0], "FlexLinePanel.Grow"));

            // The two buttons are one part, at the head's top edge, so a
            // narrow window never puts one of them on a line alone.
            Assert.Equal("HeadButtons", (string)parts[1].Attribute(X + "Name"));
            Assert.Equal("Horizontal", (string)parts[1].Attribute("Orientation"));
            Assert.Equal("Top", (string)parts[1].Attribute("VerticalAlignment"));
            var buttons = parts[1].Elements().ToList();
            Assert.Equal(2, buttons.Count);

            Assert.Equal("Profiles_GetProfiles", (string)buttons[0].Attribute("AutomationProperties.AutomationId"));
            Assert.Equal("{Binding GetProfilesCommand}", (string)buttons[0].Attribute("Command"));
            Assert.Equal("Starters", (string)buttons[0].Attribute("CommandParameter"));
            Assert.Equal("{Binding GetProfiles_Title" + S, (string)buttons[0].Attribute("Content"));
            Assert.Contains(buttons[0].Descendants(), e => (string)e.Attribute("Data") == "{StaticResource DownloadLineGeometry}");

            Assert.Equal("Profiles_NewProfile", (string)buttons[1].Attribute("AutomationProperties.AutomationId"));
            Assert.Equal("{Binding NewProfileCommand}", (string)buttons[1].Attribute("Command"));
            Assert.Equal("{StaticResource EmberPrimaryButton}", (string)buttons[1].Attribute("Style"));
            Assert.Equal("{Binding Profiles_New" + S, (string)buttons[1].Attribute("Content"));
            Assert.Contains(buttons[1].Descendants(), e => (string)e.Attribute("Data") == "{StaticResource PlusLineGeometry}");
        }

        [Fact]
        public void TheCards_AreATileGridOfProfilesThenTheDropTile()
        {
            string text = Text();
            var page = Page();
            var list = Named(page, "ProfileListBox");
            Assert.Equal("ItemsControl", list.Name.LocalName);
            var panel = Assert.Single(list.Elements().Single(e => e.Name.LocalName == "ItemsControl.ItemsPanel").Descendants(),
                e => e.Name.LocalName == "TileGridPanel");
            // The board's cards: at least 300 wide, 16 apart.
            Assert.Equal("300", (string)panel.Attribute("MinItemWidth"));
            Assert.Equal("16", (string)panel.Attribute("Gap"));

            var items = Assert.Single(list.Descendants(), e => e.Name.LocalName == "CompositeCollection").Elements().ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal("ProfilesCollectionContainer", (string)items[0].Attribute(X + "Name"));
            // The drop tile takes what is left of its row, and alone on a
            // line it is a band as tall as its words: it has no height of
            // its own. Beside cards it is as tall as they are.
            Assert.Equal("True", Attached(items[1], "TileGridPanel.TakesRest"));
            var tile = Named(items[1], "DropTile");
            Assert.Equal("Profiles_DropTile", (string)tile.Attribute("AutomationProperties.AutomationId"));
            Assert.Equal("DropTile_Click", (string)tile.Attribute("Click"));
            Assert.Null(tile.Attribute("MinHeight"));
            Assert.Equal("12", (string)tile.Attribute("CornerRadius"));
            var outline = Assert.Single(tile.Descendants(), e => e.Name.LocalName == "Rectangle");
            Assert.Equal("{DynamicResource DashLineBrush}", (string)outline.Attribute("Stroke"));
            Assert.Equal("12", (string)outline.Attribute("RadiusX"));

            // The page holds no list box and no selection: a card acts on its
            // own profile.
            Assert.DoesNotContain("<ListBox", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SelectedItem=", text, StringComparison.Ordinal);

            // The drop host keeps its four handlers and the cue over the cards.
            var host = Named(page, "ProfileGridHost");
            Assert.Equal("True", (string)host.Attribute("AllowDrop"));
            foreach (string handler in new[] { "DragEnter", "DragOver", "DragLeave", "Drop" })
                Assert.StartsWith("ProfileList_", (string)host.Attribute(handler));
            Assert.Equal("12", (string)Named(host, "ProfileDropOverlay").Elements().First().Attribute("RadiusX"));

            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ProfilesPage.xaml.cs");
            Assert.Contains("ProfilesCollectionContainer.Collection = vm.ProfileItems;", code, StringComparison.Ordinal);
        }

        [Fact]
        public void ACard_ReadsAsItsBoardDrawsIt()
        {
            var page = Page();
            var card = Card(page);
            Assert.Equal("12", (string)card.Attribute("CornerRadius"));
            Assert.Equal("ProfileCard_MouseLeftButtonDown", (string)card.Attribute("MouseLeftButtonDown"));

            var layers = Assert.Single(card.Elements(), e => e.Name.LocalName == "Grid").Elements().ToList();
            Assert.Equal(new[] { "Halo", "Border", "Grid" }, layers.Select(e => e.Name.LocalName));
            var grid = layers[2];
            // The board's padding: 18 at the left, 16 over the name, 12 at
            // the right and under the foot.
            Assert.Equal("18,16,12,12", (string)grid.Attribute("Margin"));
            var rows = grid.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
            // The name with Built-in, the two game lines, the chips, the foot.
            Assert.Equal(new[] { "DockPanel", "TextBlock", "Grid", "StackPanel", "DockPanel" }, rows.Select(e => e.Name.LocalName));
            var name = Assert.Single(rows[0].Elements(), e => (string)e.Attribute("Text") == "{Binding Name}");
            Assert.Equal("{StaticResource HeroCardTitle}", (string)name.Attribute("Style"));
            var builtIn = Assert.Single(rows[0].Elements(), e => e.Name.LocalName == "Border");
            Assert.Equal("{StaticResource SmallTag}", (string)builtIn.Attribute("Style"));
            Assert.Equal("{Binding IsDefault, Converter={StaticResource BoolToVisibilityConverter}}", (string)builtIn.Attribute("Visibility"));
            Assert.Contains(builtIn.Descendants(), e => (string)e.Attribute("Text") == "{Binding Profiles_BuiltIn" + S);
            Assert.Equal("{Binding Profiles_Card_NoGame" + S, (string)rows[1].Attribute("Text"));
            Assert.Equal("{Binding Executables}", (string)rows[2].Attribute("ToolTip"));
            Assert.Equal(new[] { "{Binding GameLead}", "{Binding GameNames}", "{Binding GameExtra}", "{Binding GameTail}" },
                rows[2].Elements().Where(e => e.Name.LocalName == "TextBlock").Select(e => (string)e.Attribute("Text")));
            Assert.Contains(rows[3].Descendants(), e => (string)e.Attribute("ItemsSource") == "{Binding TypeChips}");
            Assert.Contains(rows[3].Descendants(), e => (string)e.Attribute("Visibility") == "{Binding HasNoSlots, Converter={StaticResource BoolToVisibilityConverter}}");
            // A type and its count are one pill, the board's 26 tall.
            Assert.All(rows[3].Descendants().Where(e => e.Name.LocalName == "Border"),
                chip => Assert.Equal("{StaticResource TypeChip}", (string)chip.Attribute("Style")));
            var chipStyle = Assert.Single(page.Root.Elements().Single(e => e.Name.LocalName == "UserControl.Resources").Elements(),
                e => (string)e.Attribute(X + "Key") == "TypeChip");
            Assert.Contains(chipStyle.Elements(), s => (string)s.Attribute("Property") == "Height" && (string)s.Attribute("Value") == "26");

            // The foot: 18 under the chips at the least (the board's gap of 8
            // and its own 10), 32 tall.
            var foot = rows[4];
            Assert.Equal("32", (string)foot.Attribute("MinHeight"));
            Assert.Equal("0,18,0,0", (string)foot.Attribute("Margin"));
            var load = ById(foot, "Profiles_Load");
            Assert.Equal("{Binding Profiles_Load" + S, (string)load.Attribute("Content"));
            Assert.Equal("32", (string)load.Attribute("MinHeight"));
            Assert.Equal("{Binding DataContext.LoadProfileCommand, ElementName=ProfileListBox}", (string)load.Attribute("Command"));
            Assert.Equal("{Binding}", (string)load.Attribute("CommandParameter"));
            Assert.Equal("{Binding ShowsLoad, Converter={StaticResource BoolToVisibilityConverter}}", (string)load.Attribute("Visibility"));
            Assert.Contains(foot.Descendants(), e => (string)e.Attribute("Text") == "{Binding Profiles_Active" + S);

            var more = ById(foot, "Profiles_More");
            Assert.Equal("CardMore_Click", (string)more.Attribute("Click"));
            Assert.Equal("{StaticResource CardIconButton}", (string)more.Attribute("Style"));
            var spoken = Assert.Single(more.Descendants(), e => e.Name.LocalName == "MultiBinding");
            Assert.Contains(spoken.Elements(), b => (string)b.Attribute("Path") == "Profiles_CardMore_Format");
        }

        /// <summary>The active card is lit, as the board draws it: an ember
        /// edge, a light around it and a warm wash from its upper corner.
        /// The board's shadow of 34 at 0.15 is a halo of 42.5 at 0.075, and
        /// its ring of one pixel at 0.18 is the halo's rim. No other card
        /// has any of the three.</summary>
        [Fact]
        public void TheActiveCard_IsLit()
        {
            var card = Card(Page());
            var edge = Assert.Single(card.Elements().Single(e => e.Name.LocalName == "Border.Style").Descendants(),
                e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding IsActive}", (string)edge.Attribute("Binding"));
            Assert.Equal("True", (string)edge.Attribute("Value"));
            Assert.Equal("{DynamicResource EmberRingBrush}", (string)Assert.Single(edge.Elements()).Attribute("Value"));

            var light = Named(card, "ActiveLight");
            Assert.Equal("Halo", light.Name.LocalName);
            Assert.Equal("{Binding IsActive}", (string)light.Attribute("IsLit"));
            Assert.Equal("42.5", (string)light.Attribute("Radius"));
            Assert.Equal("0.075", (string)light.Attribute("Strength"));
            Assert.Equal("0.18", (string)light.Attribute("Rim"));
            Assert.Equal("12", (string)light.Attribute("CornerRadius"));
            Assert.Equal("-1", (string)light.Attribute("Margin"));
            Assert.Equal("False", (string)light.Attribute("IsHitTestVisible"));

            var wash = Named(card, "ActiveWash");
            Assert.Equal("{Binding IsActive, Converter={StaticResource BoolToVisibilityConverter}}", (string)wash.Attribute("Visibility"));
            Assert.Equal("False", (string)wash.Attribute("IsHitTestVisible"));
            var brush = Assert.Single(wash.Descendants(), e => e.Name.LocalName == "RadialGradientBrush");
            Assert.Equal("0,0", (string)brush.Attribute("Center"));
            Assert.Equal("0.9", (string)brush.Attribute("RadiusX"));
            Assert.Equal("1.2", (string)brush.Attribute("RadiusY"));
            Assert.Equal(new[] { "#21FF6B2C|0", "#00FF6B2C|0.62" },
                brush.Elements().Select(s => (string)s.Attribute("Color") + "|" + (string)s.Attribute("Offset")));
        }

        [Fact]
        public void TheMoreMenu_HoldsEditSaveAsSaveHereExportDelete_InThatOrder()
        {
            var menu = Assert.Single(Card(Page()).Descendants(), e => e.Name.LocalName == "ContextMenu");
            var items = menu.Elements().ToList();
            Assert.Equal(new[] { "MenuItem", "MenuItem", "MenuItem", "MenuItem", "Separator", "MenuItem" }, items.Select(e => e.Name.LocalName));
            Assert.Equal(new[] { "Edit", "SaveAs", "SaveHere", "Export", null, "Delete" }, items.Select(e => (string)e.Attribute("Tag")));
            Assert.Equal(new[] { "Profiles_Menu_Edit", "Profiles_Menu_SaveAs", "Profiles_Menu_SaveHere", "Profiles_Menu_Export", null, "Profiles_Menu_Delete" },
                items.Select(e => (string)e.Attribute("AutomationProperties.AutomationId")));
            string Gate(string flag) => "{Binding " + flag + ", Converter={StaticResource BoolToVisibilityConverter}}";
            Assert.Equal(new[] { Gate("CanEdit"), Gate("ShowsSaveAs"), Gate("ShowsSaveHere"), null, Gate("CanEdit"), Gate("CanEdit") },
                items.Select(e => (string)e.Attribute("Visibility")));
            Assert.Equal("{DynamicResource DangerInkBrush}", (string)items[5].Attribute("Foreground"));
            Assert.All(items.Where(e => e.Name.LocalName == "MenuItem"), i => Assert.Equal("ProfileMenu_Click", (string)i.Attribute("Click")));

            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ProfilesPage.xaml.cs");
            foreach (string pair in new[]
                     {
                         "\"Edit\" => vm.EditProfileCommand", "\"SaveAs\" => vm.SaveAsProfileCommand",
                         "\"SaveHere\" => vm.SaveSetupHereCommand", "\"Export\" => vm.ExportProfileCommand",
                         "\"Delete\" => vm.DeleteProfileCommand",
                     })
                Assert.Contains(pair, code, StringComparison.Ordinal);
        }

        /// <summary>Four starter profiles stand on the page, under the user's
        /// own and over Automatic Switching, as the board draws them: a
        /// heading with one line on what they are and See All, then a tile
        /// each. A tile adds its profile, and See All opens Get Profiles on
        /// its Starters.</summary>
        [Fact]
        public void TheStarters_AreFourTilesUnderTheCards()
        {
            string text = Text();
            var page = Page();
            int At(string part)
            {
                int at = text.IndexOf(part, StringComparison.Ordinal);
                Assert.True(at >= 0, part);
                return at;
            }
            Assert.True(At("x:Name=\"ProfileGridHost\"") < At("x:Name=\"StartersHead\""));
            Assert.True(At("x:Name=\"StartersHead\"") < At("x:Name=\"StarterTiles\""));
            Assert.True(At("x:Name=\"StarterTiles\"") < At("x:Name=\"AutoSwitchCard\""));

            var head = Named(page, "StartersHead").Elements().ToList();
            Assert.Equal(3, head.Count);
            Assert.Equal("{Binding Profiles_Starters" + S, (string)head[0].Attribute("Text"));
            // The pages' section title: 15 SemiBold, a second-level heading.
            Assert.Equal("{StaticResource PageSectionTitle}", (string)head[0].Attribute("Style"));
            Assert.Equal("{Binding Profiles_StartersHint" + S, (string)head[1].Attribute("Text"));
            Assert.Equal("Wrap", (string)head[1].Attribute("TextWrapping"));
            Assert.Equal("{StaticResource LinkButton}", (string)head[2].Attribute("Style"));
            Assert.Equal("{Binding GetProfilesCommand}", (string)head[2].Attribute("Command"));
            Assert.Equal("Starters", (string)head[2].Attribute("CommandParameter"));
            Assert.Equal("{Binding Common_SeeAll" + S, (string)head[2].Attribute("Content"));
            Assert.Equal("Profiles_SeeAllStarters", (string)head[2].Attribute("AutomationProperties.AutomationId"));
            // The three stand on the hint's first line when it wraps.
            Assert.All(head, part => Assert.Equal("Top", (string)part.Attribute("VerticalAlignment")));

            var list = Named(page, "StarterTiles");
            var panel = Assert.Single(list.Descendants(), e => e.Name.LocalName == "TileGridPanel");
            // The board's tiles: at least 230 wide, 12 apart, sharing the
            // row's whole width, and two and two where four do not fit.
            Assert.Equal("230", (string)panel.Attribute("MinItemWidth"));
            Assert.Equal("12", (string)panel.Attribute("Gap"));
            Assert.Equal("True", (string)panel.Attribute("FillRows"));
            Assert.Equal("True", (string)panel.Attribute("Balance"));

            var tile = Assert.Single(list.Descendants(), e => e.Name.LocalName == "Button");
            Assert.Equal("{StaticResource StarterTile}", (string)tile.Attribute("Style"));
            Assert.Equal("StarterTile_Click", (string)tile.Attribute("Click"));
            Assert.Equal("Profiles_Starter", (string)tile.Attribute("AutomationProperties.AutomationId"));
            // A screen reader hears the starter's name, and its line as help.
            Assert.StartsWith("{Binding Name,", (string)tile.Attribute("AutomationProperties.Name"));
            Assert.StartsWith("{Binding Description,", (string)tile.Attribute("AutomationProperties.HelpText"));
            Assert.Equal(new[] { "{Binding Name, Mode=OneWay}", "{Binding OutputLabel, Mode=OneWay}", "{Binding Description, Mode=OneWay}" },
                tile.Descendants().Where(e => e.Name.LocalName == "TextBlock").Select(e => (string)e.Attribute("Text")));

            // The tiles are the catalog's first four, which are the four the
            // board draws.
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ProfilesPage.xaml.cs");
            Assert.Contains("StarterTiles.ItemsSource = StarterProfileCatalog.All.Take(StarterTileCount).ToArray();", code, StringComparison.Ordinal);
            Assert.Equal(4, ProfilesPage.StarterTileCount);
            Assert.Equal(new[] { "desktop", "wasd", "hotbar", "pointclick" },
                StarterProfileCatalog.All.Take(ProfilesPage.StarterTileCount).Select(s => s.Key));

            // A tile's click adds the profile through the steps Get Profiles
            // takes, without loading it, and the message line says so.
            string main = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("ProfilesPageView.AddStarter = info => _viewModel.StatusText = string.Format(", main, StringComparison.Ordinal);
            Assert.Contains("Strings.Instance.Status_StarterImported_Format, AddWorkshopProfile(info.Build(), false));", main, StringComparison.Ordinal);
        }

        /// <summary>A click on a starter's tile adds its profile once. The
        /// second click of a double click on the same tile adds nothing, nor
        /// does a held Enter's repeat: each would add the profile again
        /// under a numbered name.</summary>
        [Theory]
        // The first click, with nothing before it.
        [InlineData("desktop", null, 1000L, 0L, true)]
        // The second click of a double click, and one at the double-click time itself.
        [InlineData("desktop", "desktop", 1400L, 1000L, false)]
        [InlineData("desktop", "desktop", 1500L, 1000L, false)]
        // Past it, the same tile adds again.
        [InlineData("desktop", "desktop", 1501L, 1000L, true)]
        // Another tile adds at once.
        [InlineData("wasd", "desktop", 1100L, 1000L, true)]
        public void AStartersTile_AddsOnce_ForADoubleClick(string key, string last, long now, long lastAt, bool adds)
            => Assert.Equal(adds, ProfilesPage.AddsStarter(key, last, now, lastAt, 500));

        /// <summary>The click's code keeps the time of every click on a tile,
        /// counted or not, so a held key's repeats never add up to a second
        /// profile.</summary>
        [Fact]
        public void AStartersTile_RemembersEveryClick()
        {
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ProfilesPage.xaml.cs");
            int at = code.IndexOf("private void StarterTile_Click(", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = code.Substring(at, 700);
            int decides = body.IndexOf("bool adds = AddsStarter(info.Key, _lastStarterKey, now, _lastStarterAt, GetDoubleClickTime());", StringComparison.Ordinal);
            int keeps = body.IndexOf("_lastStarterAt = now;", StringComparison.Ordinal);
            int acts = body.IndexOf("if (adds) AddStarter?.Invoke(info);", StringComparison.Ordinal);
            Assert.True(decides > 0 && decides < keeps && keeps < acts);
        }

        [Fact]
        public void AutomaticSwitching_IsOneLineWithTheReadout()
        {
            string text = Text();
            var card = Named(Page(), "AutoSwitchCard");
            Assert.Equal("{StaticResource CardBorder}", (string)card.Attribute("Style"));
            Assert.Equal("20,16,14,16", (string)card.Attribute("Padding"));
            Assert.Equal("0,24,0,0", (string)card.Attribute("Margin"));
            // The card has one setting, and the setting's reset is the
            // card's: no Reset All beside it.
            Assert.DoesNotContain(card.Descendants(), e => (string)e.Attribute("Style") == "{StaticResource ResetAllButton}");
            Assert.DoesNotContain("Profiles_ResetAutoSwitch", text, StringComparison.Ordinal);

            var line = Assert.Single(card.Elements());
            Assert.Equal("FlexLinePanel", line.Name.LocalName);
            var parts = line.Elements().ToList();
            Assert.Equal(2, parts.Count);

            // The title, and under it what is in front now, while the
            // setting is on.
            var title = parts[0].Elements().First();
            Assert.Equal("{Binding Profiles_AutoSwitchTitle" + S, (string)title.Attribute("Text"));
            Assert.Equal("{StaticResource CardTitle}", (string)title.Attribute("Style"));
            Assert.Equal("15", (string)title.Attribute("FontSize"));
            var inFront = Named(parts[0], "InFrontLine");
            Assert.Equal("{Binding ShowsInFront, Converter={StaticResource BoolToVisibilityConverter}}", (string)inFront.Attribute("Visibility"));
            // A cold dot, lit in its own color.
            var dot = Named(inFront, "InFrontDot");
            Assert.Equal("{DynamicResource ColdBrush}", (string)dot.Attribute("Fill"));
            Assert.Equal("Halo", dot.Parent.Name.LocalName);
            Assert.Equal("True", (string)dot.Parent.Attribute("IsLit"));
            Assert.Equal("{Binding Fill.Color, ElementName=InFrontDot}", (string)dot.Parent.Attribute("Color"));
            ById(inFront, "Profiles_InFront");
            // The three runs on one line, so no space falls between them.
            Assert.Contains("<Run Text=\"{Binding ForegroundLead, Mode=OneWay}\"/><Run Text=\"{Binding ForegroundExeName, Mode=OneWay}\"", text, StringComparison.Ordinal);
            Assert.Contains("Foreground=\"{DynamicResource ColdBrush}\"/><Run Text=\"{Binding ForegroundTail, Mode=OneWay}\"/>", text, StringComparison.Ordinal);

            // The setting: its name, its switch and its reset in one part,
            // which Search reads as the setting's row.
            var row = parts[1].Elements().ToList();
            Assert.Equal(new[] { "TextBlock", "CheckBox", "SettingResetButton" }, row.Select(e => e.Name.LocalName));
            Assert.Null(parts[1].Attribute(X + "Name"));
            Assert.Equal("{Binding Profiles_AutoSwitch" + S, (string)row[0].Attribute("Text"));
            Assert.Equal("{Binding EnableAutoProfileSwitching}", (string)row[1].Attribute("IsChecked"));
            Assert.Equal("Profiles_AutoSwitch", (string)row[1].Attribute("AutomationProperties.AutomationId"));
            Assert.Equal("{StaticResource SwitchCheckBox}", (string)row[1].Attribute("Style"));
            Assert.Equal("Reset_SettingsViewModel_EnableAutoProfileSwitching_75", (string)row[2].Attribute("AutomationProperties.AutomationId"));
            Assert.Equal("EnableAutoProfileSwitching", (string)row[2].Attribute("CommandParameter"));
        }

        [Fact]
        public void AShortcutRow_IsItsBoardsLine()
        {
            var card = Named(Page(), "ShortcutsCard");
            Assert.Equal("20,16,14,14", (string)card.Attribute("Padding"));
            Assert.Equal("0,12,0,0", (string)card.Attribute("Margin"));
            var resetAll = ById(card, "Profiles_ResetShortcuts");
            Assert.Equal("{Binding ResetShortcutsCommand}", (string)resetAll.Attribute("Command"));

            var line = Named(card, "ShortcutLine");
            Assert.Equal("FlexLinePanel", line.Name.LocalName);
            Assert.Equal("True", (string)line.Attribute("EndsApart"));
            // Controls of 34 in lines of 40, with 2 over and under the
            // shortcut: 6 between the lines of one shortcut, 10 between two.
            Assert.Equal("40", (string)line.Attribute("MinLineHeight"));
            Assert.Equal("0", (string)line.Attribute("LineGap"));
            Assert.Equal("0,2", (string)line.Attribute("Margin"));
            Assert.Equal("14", (string)line.Attribute("Gap"));
            var parts = line.Elements().ToList();
            Assert.Equal(5, parts.Count);
            // Every picker keeps its reset, and the recorded buttons keep
            // Record and Clear, as every setting and every input of the
            // program does.
            string[] ids =
            {
                "Shortcut_Mode", "Reset_ProfileShortcutViewModel_SwitchMode_808",
                "Shortcut_Profile", "Reset_ProfileShortcutViewModel_TargetProfileId_826",
                "Shortcut_Combo", "Shortcut_Record", "Shortcut_Clear",
                "Shortcut_Device", "Reset_ProfileShortcutViewModel_TriggerDeviceGuid_835",
                "Shortcut_Delete",
            };
            Assert.Equal(ids, line.Descendants().Select(e => (string)e.Attribute("AutomationProperties.AutomationId")).Where(id => id != null));
            Assert.All(line.Descendants().Where(e => e.Name.LocalName == "ComboBox"), c =>
            {
                Assert.Equal("34", (string)c.Attribute("MinHeight"));
                Assert.NotNull(c.Attribute("AutomationProperties.Name"));
            });

            // The recorded buttons are a key each, with a plus before every
            // key but the first, in a panel as wide as its keys. The
            // sentence is the list's name and its tooltip.
            var keys = ById(line, "Shortcut_Combo");
            Assert.Equal("ItemsControl", keys.Name.LocalName);
            Assert.Equal("{Binding ComboParts}", (string)keys.Attribute("ItemsSource"));
            Assert.Equal("{Binding ButtonComboDisplay}", (string)keys.Attribute("ToolTip"));
            Assert.StartsWith("{Binding ButtonComboDisplay,", (string)keys.Attribute("AutomationProperties.Name"));
            Assert.Contains(keys.Descendants(), e => e.Name.LocalName == "WrapPanel");
            var key = Assert.Single(keys.Descendants(), e => (string)e.Attribute("Style") == "{StaticResource ShortcutKey}");
            Assert.Equal(new[] { "{Binding Name, Mode=OneWay}", "{Binding DeviceTail, Mode=OneWay}" },
                key.Descendants().Where(e => e.Name.LocalName == "Run").Select(e => (string)e.Attribute("Text")));
            var first = Assert.Single(keys.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding RelativeSource={RelativeSource PreviousData}}", (string)first.Attribute("Binding"));
            Assert.Contains(first.Elements(), s => (string)s.Attribute("TargetName") == "Plus" && (string)s.Attribute("Value") == "Collapsed");
            Assert.Equal("{Binding ComboNote}", (string)Named(line, "ComboNote").Attribute("Text"));

            var record = ById(line, "Shortcut_Record");
            Assert.Equal("{StaticResource RecordButton}", (string)record.Attribute("Style"));
            Assert.Equal("ShortcutLearn_Click", (string)record.Attribute("Click"));
            Assert.Equal("{Binding LearnButtonIcon}", (string)record.Attribute("Content"));
            Assert.Equal("{Binding ClearCommand}", (string)ById(line, "Shortcut_Clear").Attribute("Command"));
            var delete = ById(line, "Shortcut_Delete");
            Assert.Equal("True", Attached(delete, "FlexLinePanel.AtEnd"));
            Assert.Equal("{Binding DeleteCommand}", (string)delete.Attribute("Command"));
            Assert.Contains(parts[2].Elements(), e => (string)e.Attribute("Text") == "{Binding Profiles_Shortcut_When" + S);
            Assert.Contains(parts[3].Elements(), e => (string)e.Attribute("Text") == "{Binding Profiles_Shortcut_On" + S);

            var add = ById(card, "Profiles_AddShortcut");
            Assert.Equal("AddShortcut_Click", (string)add.Attribute("Click"));
            Assert.Equal("0,8,0,0", (string)add.Attribute("Margin"));
            Assert.Equal("8,0", (string)add.Attribute("Padding"));
        }

        [Fact]
        public void ExternalControl_FoldsToOneLine()
        {
            var page = Page();
            var fold = Named(page, "ExternalControlFold");
            Assert.Equal("FoldList", fold.Parent.Name.LocalName);
            Assert.Equal("Profiles.ExternalControl", (string)fold.Attribute("StateKey"));
            Assert.Equal("52", (string)fold.Attribute("HeaderMinHeight"));
            Assert.Equal("{Binding Profiles_ExternalControlTitle" + S, (string)fold.Attribute("Header"));
            Assert.Equal("{Binding Profiles_ExternalControl" + S, (string)fold.Attribute("Hint"));
            Assert.Equal("{Binding Profiles_ExternalControlTooltip" + S, (string)fold.Attribute("Description"));
            Assert.Equal("{StaticResource OnOffTextConverter}",
                (string)Assert.Single(fold.Descendants(), e => e.Name.LocalName == "FoldCard.Summary").Elements().Single().Attribute("Converter"));
            // One setting, one reset: the row's. The head has no Reset All,
            // as neither board draws one (2026-11-13).
            Assert.DoesNotContain(fold.Descendants(), e => e.Name.LocalName == "FoldCard.HeaderAction");
            var check = ById(fold, "Profiles_ExternalControl");
            Assert.Equal("{Binding EnableExternalControl}", (string)check.Attribute("IsChecked"));
            var reset = Assert.Single(fold.Descendants(), e => (string)e.Attribute("CommandParameter") == "EnableExternalControl");
            Assert.Equal("Reset_SettingsViewModel_EnableExternalControl_133", (string)reset.Attribute(reset.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.AutomationId").Name));
        }

        [Fact]
        public void TheOldPage_IsGone()
        {
            string text = Text();
            foreach (string gone in new[]
                     {
                         "FOREGROUND", "ProfilesAuraBreatheSb", "ExeIconConverter", "BrowseStarterProfilesCommand",
                         "BrowseCommunityConfigsCommand", "ImportProfileCommand", "Profiles_Management",
                         "GlyphText Text=\"&#x", "MarqueeBehavior", "PillChip", "InputChip",
                     })
                Assert.DoesNotContain(gone, text, StringComparison.Ordinal);
        }
    }
}
