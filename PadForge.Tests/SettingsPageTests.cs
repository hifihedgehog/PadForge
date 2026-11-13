using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Controls;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Settings page (#574): its search's words and matching, the item
    /// that keeps its own rule while the search hides it, and what an item
    /// says to the search.
    /// </summary>
    public class SettingsPageTests
    {
        /// <summary>WPF elements need an STA thread.</summary>
        internal static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            Assert.True(thread.Join(30000), "the settings page test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public void SettingSearch_Words_DropWordsOfSymbolsAlone()
        {
            Assert.Equal(new[] { "g-keys", "tray" }, SettingSearch.Words("  g-keys  &  tray "));
            Assert.Empty(SettingSearch.Words("&&"));
            Assert.Empty(SettingSearch.Words("   "));
            Assert.Empty(SettingSearch.Words(null));
        }

        [Fact]
        public void SettingSearch_Matches_EveryWord_IgnoringCaseAccentsWidthAndSymbols()
        {
            var en = CultureInfo.GetCultureInfo("en-US");
            var fr = CultureInfo.GetCultureInfo("fr-FR");
            var ja = CultureInfo.GetCultureInfo("ja-JP");
            Assert.True(SettingSearch.Matches("Read Logitech G-Keys", SettingSearch.Words("gkeys"), en));
            Assert.True(SettingSearch.Matches("Piloter les écrans de clavier", SettingSearch.Words("ÉCRANS"), fr));
            Assert.True(SettingSearch.Matches("Minimize to System Tray", SettingSearch.Words("ｔｒａｙ"), en));
            Assert.True(SettingSearch.Matches("キーボード画面を制御する", SettingSearch.Words("画面"), ja));
            // Every word must be there.
            Assert.False(SettingSearch.Matches("Start at Login", SettingSearch.Words("tray login"), en));
            Assert.True(SettingSearch.Matches("Start at Login", SettingSearch.Words("login START"), en));
            // No words match nothing.
            Assert.False(SettingSearch.Matches("Start at Login", SettingSearch.Words(""), en));
            Assert.False(SettingSearch.Matches("Start at Login", null, en));
        }

        private sealed class Gate : System.ComponentModel.INotifyPropertyChanged
        {
            private Visibility _shown = Visibility.Collapsed;
            public Visibility Shown
            {
                get => _shown;
                set
                {
                    _shown = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Shown)));
                }
            }
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        }

        [Fact]
        public void SettingItem_KeepsItsOwnRule_AndTheFilterWins() => RunSta(() =>
        {
            var gate = new Gate();
            var item = new SettingItem();
            BindingOperations.SetBinding(item, UIElement.VisibilityProperty, new Binding(nameof(Gate.Shown)) { Source = gate });
            Assert.Equal(Visibility.Collapsed, item.Visibility);
            Assert.Equal(Visibility.Collapsed, item.OwnVisibility);

            gate.Shown = Visibility.Visible;
            Assert.Equal(Visibility.Visible, item.Visibility);
            item.IsFilteredOut = true;
            Assert.Equal(Visibility.Collapsed, item.Visibility);
            Assert.Equal(Visibility.Visible, item.OwnVisibility);
            item.IsFilteredOut = false;
            Assert.Equal(Visibility.Visible, item.Visibility);

            // The page's rule still moves the item while it is filtered out,
            // and shows through once the search lets it go.
            item.IsFilteredOut = true;
            gate.Shown = Visibility.Collapsed;
            Assert.Equal(Visibility.Collapsed, item.OwnVisibility);
            item.IsFilteredOut = false;
            Assert.Equal(Visibility.Collapsed, item.Visibility);
        });

        [Fact]
        public void SettingSearch_TextOf_LeavesOutResets_AndCollapsedParts() => RunSta(() =>
        {
            var item = new SettingItem();
            var row = new SettingRow();
            row.Children.Add(new TextBlock { Text = "Start Minimized", ToolTip = "Opens in the tray" });
            var reset = new SettingResetButton { SettingLabel = "Start Minimized" };
            Assert.StartsWith("Reset", (string)reset.ToolTip, StringComparison.Ordinal);
            row.Children.Add(reset);
            row.Children.Add(new Button { Content = "Hidden Word", Visibility = Visibility.Collapsed });
            row.Children.Add(new CheckBox { Content = "Switch Word" });
            item.Children.Add(row);
            SettingSearch.SetTerms(item, "Synonym");

            string text = SettingSearch.TextOf(item);
            Assert.Contains("Start Minimized", text, StringComparison.Ordinal);
            Assert.Contains("Opens in the tray", text, StringComparison.Ordinal);
            Assert.Contains("Switch Word", text, StringComparison.Ordinal);
            Assert.Contains("Synonym", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Reset", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Hidden", text, StringComparison.Ordinal);

            // The item's own rule is the filter's to read, not the text's.
            item.Visibility = Visibility.Collapsed;
            Assert.Contains("Start Minimized", SettingSearch.TextOf(item), StringComparison.Ordinal);
        });

        /// <summary>Each card's Reset All puts every setting it shows back,
        /// through the settings' own resets. None of these setters reaches a
        /// static. Hardware's is pinned by the page's markup and its rows'
        /// own resets.</summary>
        [Fact]
        public void ResetCard_PutsEachSettingBackToItsDefault()
        {
            var vm = new SettingsViewModel
            {
                MinimizeToTray = true, CloseToTray = true, AlwaysShowTrayIcon = true, StartMinimized = true, StartAtLogin = true,
                CheckForUpdatesAutomatically = false, InstallUpdatesAutomatically = true, IncludePreReleaseUpdates = true,
                AutoStartEngine = false, EnablePollingOnFocusLoss = false, PollingRateMs = 8, HmInactivityDestroyTimeoutSeconds = 5,
                AssignOfferNewDevice = false, AssignOfferEmptySlot = false, AssignOfferHome = false,
                BatteryNotifyEnabled = false, BatteryNotifyThreshold = 30, BatteryNotifyVibrate = true,
                BatteryNotifyRepeatMinutes = 10, BatteryNotifyFull = true,
            };
            foreach (string card in new[] { "Window", "Updates", "InputEngine", "AssignmentPrompts", "BatteryAlerts" })
            {
                Assert.True(vm.ResetCardCommand.CanExecute(card), card);
                vm.ResetCardCommand.Execute(card);
            }

            Assert.False(vm.MinimizeToTray);
            Assert.False(vm.CloseToTray);
            Assert.False(vm.AlwaysShowTrayIcon);
            Assert.False(vm.StartMinimized);
            Assert.False(vm.StartAtLogin);
            Assert.True(vm.CheckForUpdatesAutomatically);
            Assert.False(vm.InstallUpdatesAutomatically);
            Assert.False(vm.IncludePreReleaseUpdates);
            Assert.True(vm.AutoStartEngine);
            Assert.True(vm.EnablePollingOnFocusLoss);
            Assert.Equal(1, vm.PollingRateMs);
            Assert.Equal(60, vm.HmInactivityDestroyTimeoutSeconds);
            Assert.True(vm.AssignOfferNewDevice);
            Assert.True(vm.AssignOfferEmptySlot);
            Assert.True(vm.AssignOfferHome);
            Assert.True(vm.BatteryNotifyEnabled);
            Assert.Equal(15, vm.BatteryNotifyThreshold);
            Assert.False(vm.BatteryNotifyVibrate);
            Assert.Equal(0, vm.BatteryNotifyRepeatMinutes);
            Assert.False(vm.BatteryNotifyFull);

            // A card with no Reset All has nothing to put back.
            Assert.False(vm.ResetCardCommand.CanExecute("Language"));
            Assert.Empty(SettingsViewModel.CardSettings(null));

            // Close to System Tray's own reset is the generic one now.
            Assert.True(SettingsViewModel.CanResetSetting(nameof(SettingsViewModel.CloseToTray)));
            vm.CloseToTray = true;
            vm.ResetCloseToTrayCommand.Execute(null);
            Assert.False(vm.CloseToTray);
        }

        private static readonly System.Xml.Linq.XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static string PageText() => ExtrasPageTests.RepoText("PadForge.App", "Views", "SettingsPage.xaml");

        /// <summary>Each card's Reset All puts back the settings its card
        /// shows, no more and no fewer: the resets inside the card, and Close
        /// to System Tray for the reset that names no parameter.</summary>
        [Fact]
        public void EveryCardsResetAll_ResetsExactlyTheRowsItShows()
        {
            var page = System.Xml.Linq.XDocument.Parse(PageText());
            var resetAlls = page.Descendants().Where(e => (string)e.Attribute("Command") == "{Binding ResetCardCommand}").ToList();
            var keys = new List<string>();
            foreach (var button in resetAlls)
            {
                string key = (string)button.Attribute("CommandParameter");
                keys.Add(key);
                Assert.Equal("Settings_ResetAll_" + key, (string)button.Attribute("AutomationProperties.AutomationId"));
                var card = button.Ancestors().First(e => e.Attributes().Any(a => a.Name.LocalName == "SettingSearch.Card"));
                var shown = card.Descendants().Where(e => e.Name.LocalName == "SettingResetButton")
                    .Select(e => (string)e.Attribute("CommandParameter")
                                 ?? ((string)e.Attribute(X + "Name") == "CloseToTrayReset" ? "CloseToTray" : null))
                    .ToList();
                Assert.DoesNotContain(null, shown);
                Assert.Equal(SettingsViewModel.CardSettings(key).OrderBy(s => s, StringComparer.Ordinal),
                             shown.OrderBy(s => s, StringComparer.Ordinal));
            }
            Assert.Equal(new[] { "Window", "Updates", "InputEngine", "AssignmentPrompts", "BatteryAlerts", "Hardware" }, keys);
        }

        /// <summary>The Settings page holds the app's own settings. Nothing a
        /// single device or controller owns is set there (owner, 2026-10-25).</summary>
        [Fact]
        public void ThePageHasNoDeviceLevelSetting()
        {
            string page = PageText();
            foreach (string model in new[] { "DevicesViewModel", "DeviceRowViewModel", "PadViewModel" })
                Assert.DoesNotContain(model, page, StringComparison.Ordinal);
            var paths = System.Text.RegularExpressions.Regex.Matches(page, @"\{Binding\s+(?:Path=)?([A-Za-z_.]+)")
                .Select(m => m.Groups[1].Value).ToList();
            Assert.True(paths.Count > 100, "the scan is not reading the page's bindings");
            foreach (string path in paths)
                Assert.DoesNotContain(path.Split('.')[0],
                    new[] { "Devices", "DeviceRows", "Pads", "SelectedPad", "BlissBoxPorts", "KeyboardScreens" });
        }

        /// <summary>Everything the search shows or hides is an item, every card
        /// and the Drivers card name themselves, the five groups name
        /// themselves, and the box is reachable by Ctrl+F.</summary>
        [Fact]
        public void EverySearchableRowIsAnItem()
        {
            var page = System.Xml.Linq.XDocument.Parse(PageText());
            var rows = page.Descendants().Where(e => e.Name.LocalName == "SettingRow"
                || (e.Name.LocalName == "FlexLinePanel" && (string)e.Attribute("Style") == "{StaticResource DriverRow}")).ToList();
            Assert.True(rows.Count > 40, rows.Count + " rows");
            Assert.All(rows, r => Assert.Contains(r.Ancestors(), a => a.Name.LocalName == "SettingItem"));

            var cards = page.Descendants().Where(e => (e.Name.LocalName == "Border" && (string)e.Attribute("Style") == "{StaticResource CardBorder}")
                                                      || e.Name.LocalName == "FoldList").ToList();
            Assert.Equal(12, cards.Count);
            Assert.All(cards, c => Assert.Contains(c.Attributes(), a => a.Name.LocalName == "SettingSearch.Card"));
            foreach (string group in new[] { "General", "Input", "Hardware", "Drivers", "Data" })
            {
                var panel = Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == group + "Group");
                Assert.Contains(panel.Attributes(), a => a.Name.LocalName == "SettingSearch.Group");
                Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == group + "Heading");
            }

            var box = Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == "SettingsSearchBox");
            Assert.Equal("Settings_Search", (string)box.Attribute("AutomationProperties.AutomationId"));
            Assert.Equal("Page_PreviewKeyDown", (string)page.Root.Attribute("PreviewKeyDown"));
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "SettingsPage.xaml.cs");
            Assert.Contains("if (e.Key != Key.F || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;", code, StringComparison.Ordinal);
            Assert.Contains("SettingsSearchBox.Focus();", code, StringComparison.Ordinal);
        }

        /// <summary>A row of the page shares its width as its board's grid
        /// does: the label has all the room the other parts leave. In a row
        /// too narrow for the label on one line beside the part that gives
        /// way, that part gives what the row lacks, down to its least, and
        /// the label wraps in the rest.</summary>
        [Theory]
        // No part gives way: the label has what the controls leave.
        [InlineData(780.0, 150.0, 84.0, 0.0, 0.0, 696.0, 0.0)]
        [InlineData(200.0, 150.0, 84.0, 0.0, 0.0, 116.0, 0.0)]
        [InlineData(60.0, 150.0, 84.0, 0.0, 0.0, 0.0, 0.0)]
        // Room for both: the part at what it asks, the label the rest.
        [InlineData(780.0, 150.0, 84.0, 300.0, 96.0, 396.0, 300.0)]
        // Short of room: the part gives first and the label keeps its line.
        [InlineData(484.0, 150.0, 84.0, 300.0, 96.0, 150.0, 250.0)]
        // At its least, the label wraps in what is left.
        [InlineData(284.0, 150.0, 84.0, 300.0, 96.0, 104.0, 96.0)]
        // A part that asks for less than the least keeps what it asks.
        [InlineData(184.0, 150.0, 84.0, 40.0, 40.0, 60.0, 40.0)]
        // Narrower than its least: all the room there is.
        [InlineData(164.0, 150.0, 84.0, 300.0, 96.0, 0.0, 80.0)]
        public void ARowsLabelTakesTheRoom_AndThePartThatGivesWayGivesFirst(double width, double label, double fixedWidth,
            double fillWanted, double fillLeast, double expectedLabel, double expectedFill)
        {
            var (lead, fill) = SettingRow.EndsWidths(width, label, fixedWidth, fillWanted, fillLeast);
            Assert.Equal(expectedLabel, lead, 6);
            Assert.Equal(expectedFill, fill, 6);
            // Every part and nothing left over, while the row has room at all.
            if (width >= fixedWidth) Assert.Equal(width, lead + fill + fixedWidth, 6);
        }

        [Fact]
        public void ARowWithNoBound_HandsItsPartsWhatTheyAsk()
        {
            Assert.Equal((150.0, 300.0), SettingRow.EndsWidths(double.PositiveInfinity, 150, 84, 300, 96));
            Assert.Equal((150.0, 0.0), SettingRow.EndsWidths(double.NaN, 150, 84, 0, 0));
        }

        private static string Attached(System.Xml.Linq.XElement e, string property)
            => (string)e.Attributes().SingleOrDefault(a => a.Name.LocalName == property);

        /// <summary>What Is Sent is a disclosure and reads as one: it stands
        /// under the line it adds to, its words and then a chevron that
        /// turns over while it is open, the full account opens under it,
        /// and the card's two buttons follow. Beside the buttons and with
        /// no chevron it read as their name.</summary>
        [Fact]
        public void WhatIsSent_IsADisclosureUnderItsLine_AndTheButtonsFollow()
        {
            var page = System.Xml.Linq.XDocument.Parse(PageText());
            var toggle = Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == "WhatIsSentToggle");
            Assert.Equal("ToggleButton", toggle.Name.LocalName);
            Assert.Equal("{StaticResource QuietToggle}", (string)toggle.Attribute("Style"));
            Assert.Equal("Left", (string)toggle.Attribute("HorizontalAlignment"));
            // Its words start where the line's do: 10 and 10 are the 20.
            Assert.Equal("10,0", (string)toggle.Attribute("Padding"));
            Assert.Equal("10,0,10,0", (string)toggle.Attribute("Margin"));
            Assert.Contains("Settings_WhatIsSent,", Attached(toggle, "AutomationProperties.Name"));
            Assert.Null(toggle.Attribute("Content"));

            var face = Assert.Single(toggle.Elements());
            Assert.Equal("StackPanel", face.Name.LocalName);
            var parts = face.Elements().ToList();
            Assert.Equal(new[] { "TextBlock", "Path" }, parts.Select(p => p.Name.LocalName));
            Assert.Contains("Settings_WhatIsSent,", (string)parts[0].Attribute("Text"));
            Assert.Equal("WhatIsSentChevron", (string)parts[1].Attribute(X + "Name"));
            var turn = Assert.Single(parts[1].Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding IsChecked, ElementName=WhatIsSentToggle}", (string)turn.Attribute("Binding"));
            Assert.Equal("True", (string)turn.Attribute("Value"));
            Assert.Equal("180", (string)Assert.Single(turn.Descendants(), e => e.Name.LocalName == "RotateTransform").Attribute("Angle"));

            // The line, the disclosure, what it opens, the buttons.
            var item = toggle.Parent;
            Assert.Equal("SettingItem", item.Name.LocalName);
            var order = item.Elements().ToList();
            Assert.Equal(new[] { "TextBlock", "ToggleButton", "TextBlock", "FlexLinePanel" }, order.Select(p => p.Name.LocalName));
            Assert.Contains("Settings_CommunityConfigs_Line,", (string)order[0].Attribute("Text"));
            Assert.Contains("Settings_CommunityConfigsDesc,", (string)order[2].Attribute("Text"));
            Assert.Contains("IsChecked, ElementName=WhatIsSentToggle", (string)order[2].Attribute("Visibility"));
            var buttons = order[3].Elements().ToList();
            Assert.Equal(2, buttons.Count);
            Assert.All(buttons, b => Assert.Equal("Button", b.Name.LocalName));
        }

        /// <summary>Every version the page shows reads bare. Two of the
        /// four drivers' versions carried a "v" and two did not.</summary>
        [Fact]
        public void TheDriversVersions_ReadBare_AsEveryVersionOnThePage()
        {
            var vm = new PadForge.ViewModels.SettingsViewModel();
            Assert.Matches(@"^\d+\.\d+\.\d+$", vm.HIDMaestroVersion);
            Assert.Matches(@"^\d+\.\d+\.\d+$", vm.BthPS3Version);
            string model = ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "SettingsViewModel.cs");
            Assert.DoesNotContain("$\"v{", model);
        }

        /// <summary>The page as its board of the world-class pass draws it:
        /// the boards' gutters, one column of at most 1040 at the page's
        /// start, the group list 190 of it and the cards the 822 that a
        /// gap of 28 leaves (the board's 820 left them 2 short of the
        /// search box on a wide window), a title of 28 in the display face
        /// and the search field. The
        /// rows' controls stand at their rows' ends, set once on the cards'
        /// host. Nothing is left of the code that sized a label column.</summary>
        [Fact]
        public void ThePage_ReadsInItsBoardsColumn()
        {
            string text = PageText();
            var page = System.Xml.Linq.XDocument.Parse(text);
            System.Xml.Linq.XElement Named(string name) => Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == name);

            var column = Named("PageColumn");
            Assert.Equal("StackPanel", column.Name.LocalName);
            Assert.Equal("ScrollViewer", column.Parent.Name.LocalName);
            Assert.Equal("36,26,36,36", (string)column.Attribute("Margin"));
            // The cap is the panel's own, at the page's start. A grid's star
            // column that stops at a MaxWidth takes one device pixel more
            // than it under layout rounding, so the page caps no column.
            Assert.Equal("1040", (string)column.Attribute("MaxWidth"));
            Assert.Equal("Left", (string)column.Attribute("HorizontalAlignment"));
            Assert.DoesNotContain(page.Descendants(), e => e.Name.LocalName == "ColumnDefinition" && e.Attribute("MaxWidth") != null);

            var title = Named("SettingsTitle");
            Assert.Equal("28", (string)title.Attribute("FontSize"));
            Assert.Equal("{StaticResource DisplayFontFamily}", (string)title.Attribute("FontFamily"));
            Assert.Equal("300", Attached(Named("SettingsSearchBox"), "FlexLinePanel.Basis"));

            var list = Named("GroupList").Parent;
            Assert.Equal("190", (string)list.Attribute("Width"));
            Assert.Equal("190", Attached(list, "FlexLinePanel.Basis"));
            Assert.Null(Attached(list, "FlexLinePanel.Grow"));
            var host = Named("GroupsHost");
            Assert.Equal("822", (string)host.Attribute("MaxWidth"));
            Assert.Equal("True", Attached(host, "SettingRow.Ends"));
            Assert.Null(host.Attribute("SizeChanged"));
            Assert.Equal("28", (string)host.Parent.Attribute("Gap"));
            // The list, the gap and the cards are the column, to the unit.
            Assert.Equal(1040, 190 + 28 + 822);

            // A card has no padding of its own and 14 under it, and its
            // title is the board's 15.
            var resources = Assert.Single(page.Descendants(), e => e.Name.LocalName == "ScrollViewer.Resources");
            string Set(string key, string property) => (string)resources.Elements().Single(s => (string)s.Attribute(X + "Key") == key)
                .Elements().Single(s => (string)s.Attribute("Property") == property).Attribute("Value");
            Assert.Equal("0", Set("CardBorder", "Padding"));
            Assert.Equal("0,0,0,14", Set("CardBorder", "Margin"));
            Assert.Equal("15", Set("CardTitle", "FontSize"));

            // A row stands 20 in and 10 from the end, 48 tall with its 44,
            // under a hairline its item draws. Its label has no width of
            // its own, and what a row reports stands aside.
            var own = page.Root.Elements().Single(e => e.Name.LocalName == "UserControl.Resources");
            System.Xml.Linq.XElement Style(string targetOrKey) => own.Elements().Single(s =>
                (string)s.Attribute(X + "Key") == targetOrKey || (s.Attribute(X + "Key") == null && (string)s.Attribute("TargetType") == targetOrKey));
            string Of(string style, string property) => (string)Style(style).Elements().Single(s => (string)s.Attribute("Property") == property).Attribute("Value");
            Assert.Equal("20,2,10,2", Of("controls:SettingRow", "Margin"));
            Assert.Equal("{DynamicResource SteelLineSoftBrush}", Of("controls:SettingItem", "Rule"));
            Assert.Equal("Auto", Of("DzLabel", "Width"));
            Assert.DoesNotContain(Style("DzSubLabel").Elements(), s => (string)s.Attribute("Property") == "Width");
            Assert.Equal("True", Of("RowStatus", "controls:SettingRow.Aside"));
            Assert.Equal("True", Of("RowStatus", "controls:SettingRow.Fills"));
            Assert.All(page.Descendants().Where(e => e.Name.LocalName == "SettingRow" && (string)e.Attribute("MinHeight") == "44"),
                row => Assert.True(row.Attribute("Margin") == null || (string)row.Attribute("Margin") == "44,0,10,10", (string)row.Attribute("Margin")));

            // Every card's head is 52 tall: a line with Reset All at its
            // end, or the title alone with 16 over and under its 20.
            var heads = page.Descendants().Where(e => e.Name.LocalName == "DockPanel" && (string)e.Attribute("LastChildFill") == "False"
                && e.Elements().Any(c => (string)c.Attribute("Style") == "{StaticResource CardTitle}")).ToList();
            Assert.Equal(6, heads.Count);
            Assert.All(heads, h =>
            {
                Assert.Equal("52", (string)h.Attribute("MinHeight"));
                Assert.Equal("20,0,10,0", (string)h.Attribute("Margin"));
            });
            var alone = page.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource CardTitle}"
                && e.Parent.Name.LocalName == "StackPanel").ToList();
            Assert.Equal(5, alone.Count);
            Assert.All(alone, t => Assert.Equal("20,16,10,16", (string)t.Attribute("Margin")));

            foreach (string gone in new[] { "SettingsLabelWidth", "SettingsSubLabelWidth", "ControlColumnInset", "GroupsHost_SizeChanged", "xmlns:sys" })
                Assert.DoesNotContain(gone, text, StringComparison.Ordinal);
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "SettingsPage.xaml.cs");
            Assert.DoesNotContain("LabelWidthFor", code, StringComparison.Ordinal);
            Assert.DoesNotContain("GroupsHost_SizeChanged", code, StringComparison.Ordinal);
        }

        /// <summary>A group's button has its line icon and, while its group
        /// shows, the sidebar's ember bar and an ember icon.</summary>
        [Fact]
        public void AGroupsButton_HasItsIcon_AndTheSidebarsBarWhileItsGroupShows()
        {
            var page = System.Xml.Linq.XDocument.Parse(PageText());
            string shell = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Shell.xaml");
            foreach (var (button, icon) in new[]
                     {
                         ("GroupGeneral", "NavSettingsGeometry"), ("GroupInput", "NavDevicesGeometry"), ("GroupHardware", "ChipLineGeometry"),
                         ("GroupDrivers", "DownloadLineGeometry"), ("GroupData", "ShieldLineGeometry"),
                     })
            {
                var element = Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == button);
                Assert.Equal("{StaticResource " + icon + "}", Attached(element, "SettingsPage.GroupIcon"));
                Assert.Equal("{StaticResource SettingsGroupButton}", (string)element.Attribute("Style"));
                Assert.Contains("<Geometry x:Key=\"" + icon + "\">", shell, StringComparison.Ordinal);
            }
            var style = Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Key") == "SettingsGroupButton");
            var bar = Assert.Single(style.Descendants(), e => (string)e.Attribute(X + "Name") == "PickBar");
            Assert.Equal(("3", "18", "Collapsed"), ((string)bar.Attribute("Width"), (string)bar.Attribute("Height"), (string)bar.Attribute("Visibility")));
            Assert.Equal("{DynamicResource EmberBrush}", (string)bar.Attribute("Background"));
            var drawn = Assert.Single(style.Descendants(), e => (string)e.Attribute(X + "Name") == "Icon");
            Assert.Equal("{Binding (local:SettingsPage.GroupIcon), RelativeSource={RelativeSource TemplatedParent}}", (string)drawn.Attribute("Data"));
            var picked = Assert.Single(style.Descendants(), e => e.Name.LocalName == "Trigger" && (string)e.Attribute("Property") == "IsChecked");
            Assert.Contains(picked.Elements(), s => (string)s.Attribute("TargetName") == "PickBar" && (string)s.Attribute("Value") == "Visible");
            Assert.Contains(picked.Elements(), s => (string)s.Attribute("TargetName") == "Icon" && (string)s.Attribute("Value") == "{DynamicResource EmberBrush}");

            // The icon is the button's own attached property, a geometry.
            RunSta(() =>
            {
                var button = new RadioButton();
                Assert.Null(PadForge.Views.SettingsPage.GetGroupIcon(button));
                var geometry = System.Windows.Media.Geometry.Parse("M0 0 L1 1");
                PadForge.Views.SettingsPage.SetGroupIcon(button, geometry);
                Assert.Same(geometry, PadForge.Views.SettingsPage.GetGroupIcon(button));
            });
        }

        /// <summary>The Updates card opens on its status, as the board has
        /// it: on the band's fill, a mark, what the last check found or
        /// PadForge's version before any, and the actions at the row's end
        /// with Install and Restart last. The mark is the cold check once
        /// this build is the newest and the ember arrow while an update
        /// waits.</summary>
        [Fact]
        public void TheUpdatesCard_OpensOnItsStatus()
        {
            string text = PageText();
            var page = System.Xml.Linq.XDocument.Parse(text);
            var item = Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == "UpdateStatusItem");
            Assert.Equal("SettingItem", item.Name.LocalName);
            Assert.Equal("{DynamicResource SteelBandBrush}", (string)item.Attribute("Background"));
            // First under the card's head, over the three switches.
            var parts = item.Parent.Elements().ToList();
            Assert.Equal("DockPanel", parts[0].Name.LocalName);
            Assert.Same(item, parts[1]);
            Assert.Equal(5, parts.Count);

            var row = Assert.Single(item.Elements(), e => e.Name.LocalName == "SettingRow");
            Assert.Equal("56", (string)row.Attribute("MinHeight"));
            Assert.Equal("20,2,20,2", (string)row.Attribute("Margin"));
            var kids = row.Elements().ToList();
            Assert.Equal(new[] { "DockPanel", "WrapPanel" }, kids.Select(k => k.Name.LocalName));

            var triggers = kids[0].Descendants().Where(e => e.Name.LocalName == "DataTrigger"
                && e.Ancestors().Any(a => a.Name.LocalName == "DecorativeStrokeIcon.Style")).ToList();
            Assert.Equal(new[] { "{Binding IsUpToDate}", "{Binding IsUpdateAvailable}" }, triggers.Select(t => (string)t.Attribute("Binding")));
            Assert.Contains(triggers[0].Elements(), s => (string)s.Attribute("Value") == "{StaticResource CheckCircleGeometry}");
            Assert.Contains(triggers[0].Elements(), s => (string)s.Attribute("Value") == "{DynamicResource ColdBrush}");
            Assert.Contains(triggers[1].Elements(), s => (string)s.Attribute("Value") == "{StaticResource DownloadLineGeometry}");
            Assert.Contains(triggers[1].Elements(), s => (string)s.Attribute("Value") == "{DynamicResource EmberBrush}");
            Assert.Contains("<Geometry x:Key=\"CheckCircleGeometry\">", ExtrasPageTests.RepoText("PadForge.App", "Resources", "Shell.xaml"), StringComparison.Ordinal);

            // The status, read out as it changes, and the version line that
            // stands in its place while it is empty.
            var status = Assert.Single(kids[0].Descendants(), e => (string)e.Attribute(X + "Name") == "UpdateStatusLineText");
            Assert.Equal("{Binding UpdateStatusText}", (string)status.Attribute("Text"));
            Assert.Equal("True", Attached(status, "LiveText.Polite"));
            Assert.Equal("{Binding UpdateStatusText, Converter={StaticResource StringToVisibility}}", (string)status.Attribute("Visibility"));
            var version = Assert.Single(kids[0].Descendants(), e => (string)e.Attribute(X + "Name") == "UpdateVersionLine");
            Assert.Contains(version.Elements(), r => (string)r.Attribute("Text") == "{Binding VersionNumber, Mode=OneWay}");
            var shows = Assert.Single(version.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal(("{Binding UpdateStatusText}", ""), ((string)shows.Attribute("Binding"), (string)shows.Attribute("Value")));

            var buttons = kids[1].Elements().ToList();
            Assert.Equal("True", Attached(kids[1], "SettingRow.Fills"));
            Assert.Equal(new[] { "Settings_CheckNow", "Settings_ReleaseNotes", "Settings_InstallAndRestart" },
                buttons.Select(b => System.Text.RegularExpressions.Regex.Match((string)b.Attribute("Content"), @"\{Binding (\w+),").Groups[1].Value));
            Assert.Equal(new[] { "{Binding CheckForUpdatesNowCommand}", "{Binding OpenReleaseNotesCommand}", "{Binding InstallUpdateCommand}" },
                buttons.Select(b => (string)b.Attribute("Command")));
            const string waits = "{Binding IsUpdateAvailable, Converter={StaticResource BoolToVisibilityConverter}}";
            Assert.Equal(new[] { null, waits, waits }, buttons.Select(b => (string)b.Attribute("Visibility")));

            var bar = Assert.Single(item.Elements(), e => e.Name.LocalName == "ProgressBar");
            Assert.Equal("{Binding IsUpdateDownloading, Converter={StaticResource BoolToVisibilityConverter}}", (string)bar.Attribute("Visibility"));
        }

        /// <summary>A part that holds rows inside a padding of its own hands
        /// that padding back, so its rows stand as every card's rows do: the
        /// More row of Battery Alerts and HidHide's fold. The More row and
        /// the drivers' list draw hairlines of their own, so what stands
        /// under one of those draws none.</summary>
        [Fact]
        public void RowsInsideAPartOfTheirOwn_StandAsEveryRowDoes()
        {
            var page = System.Xml.Linq.XDocument.Parse(PageText());
            System.Xml.Linq.XElement Named(string name) => Assert.Single(page.Descendants(), e => (string)e.Attribute(X + "Name") == name);

            var more = Named("BatteryMore");
            Assert.Equal("0", (string)more.Attribute("Margin"));
            Assert.Equal("{x:Null}", (string)more.Parent.Attribute("Rule"));
            var under = Assert.Single(more.Elements(), e => e.Name.LocalName == "StackPanel");
            Assert.Equal("-20,-8,-12,-12", (string)under.Attribute("Margin"));
            var rows = under.Elements().ToList();
            Assert.Equal(3, rows.Count);
            Assert.Equal("{x:Null}", (string)rows[0].Attribute("Rule"));
            Assert.All(rows.Skip(1), r => Assert.Null(r.Attribute("Rule")));

            var fold = Named("HidHideOptions");
            var held = Assert.Single(fold.Elements(), e => e.Name.LocalName == "StackPanel");
            Assert.Equal("-20,0,-12,-12", (string)held.Attribute("Margin"));
            var again = Assert.Single(held.Elements().Single(e => e.Name.LocalName == "StackPanel.Resources").Elements());
            Assert.Equal("controls:SettingItem", (string)again.Attribute("TargetType"));
            Assert.Equal("{DynamicResource SteelLineSoftBrush}", (string)Assert.Single(again.Elements()).Attribute("Value"));

            var drivers = Assert.Single(page.Descendants(), e => e.Name.LocalName == "FoldList");
            Assert.Equal("14", (string)drivers.Attribute("Gap"));
            var none = Assert.Single(drivers.Elements().Single(e => e.Name.LocalName == "FoldList.Resources").Elements());
            Assert.Equal("controls:SettingItem", (string)none.Attribute("TargetType"));
            Assert.Empty(none.Elements());

            // The list of applications is a block under its name, in a well.
            var block = Named("WhitelistBlock");
            Assert.Equal("SettingItem", block.Parent.Name.LocalName);
            Assert.Equal(new[] { "TextBlock", "TextBlock", "Border", "StackPanel" }, block.Elements().Select(e => e.Name.LocalName));
            Assert.Single(block.Descendants(), e => e.Name.LocalName == "ListBox");
        }

        /// <summary>A status line takes the warning color only when there is
        /// something to fix.</summary>
        [Fact]
        public void StatusFlags_WarnOnlyWhenThereIsSomethingToFix()
        {
            foreach (PadForge.Engine.Common.Logitech.LogitechGKeyState state in
                     Enum.GetValues(typeof(PadForge.Engine.Common.Logitech.LogitechGKeyState)))
            {
                bool running = state == PadForge.Engine.Common.Logitech.LogitechGKeyState.Running;
                Assert.Equal(!running, PadForge.Services.InputService.GKeysStatusIsWarning(state, 5));
                // Running with no key seen yet warns too.
                Assert.True(PadForge.Services.InputService.GKeysStatusIsWarning(state, 0));
            }

            var loaded = new PadForge.Common.Input.Falcon.FalconSweepStatus(
                "", @"C:\Program Files\Force Dimension\sdk-3.17.1\bin\dhd64.dll", "3.17", null, 0, Array.Empty<string>(), null);
            Assert.False(PadForge.Services.InputService.FalconStatusIsWarning(loaded, 1));
            Assert.True(PadForge.Services.InputService.FalconStatusIsWarning(loaded, 0));
            Assert.True(PadForge.Services.InputService.FalconStatusIsWarning(loaded with { LibraryReason = "load" }, 1));
            Assert.True(PadForge.Services.InputService.FalconStatusIsWarning(loaded with { PickNotLoaded = true }, 1));
        }
    }

    /// <summary>
    /// The Settings page's search over a group's cards (#574): what shows,
    /// what folds open for the search, and that an opening for the search is
    /// never stored.
    /// </summary>
    [Collection("CardFoldStateStatics")]
    public class SettingSearchFilterTests
    {
        private static SettingItem Leaf(string label)
        {
            var item = new SettingItem();
            item.Children.Add(new TextBlock { Text = label });
            return item;
        }

        [Fact]
        public void SettingSearch_Filter_ShowsMatchingRows_OpensTheirFoldWithoutStoringIt() => SettingsPageTests.RunSta(() =>
        {
            CardFoldState.Load(null);
            int changes = 0;
            EventHandler counted = (_, _) => changes++;
            CardFoldState.Changed += counted;
            try
            {
                var group = new StackPanel();
                SettingSearch.SetGroup(group, "Input");
                var first = new Border();
                SettingSearch.SetCard(first, "Battery Alerts");
                var firstBody = new StackPanel();
                first.Child = firstBody;
                var low = Leaf("Notify When Battery Runs Low");
                var below = Leaf("Notify at or Below");
                var vibrate = Leaf("Also Vibrate the Controller");
                var repeat = Leaf("Repeat the Notice Every");
                var more = new MoreRow { StateKey = "t.more", Header = "More", Hint = "Also Vibrate the Controller, Repeat the Notice Every" };
                var folded = new StackPanel();
                folded.Children.Add(vibrate);
                folded.Children.Add(repeat);
                more.Content = folded;
                var container = new SettingItem();
                container.Children.Add(more);
                firstBody.Children.Add(low);
                firstBody.Children.Add(below);
                firstBody.Children.Add(container);
                group.Children.Add(first);
                var second = new Border { Child = Leaf("Offer New Devices to the Open Virtual Controller") };
                SettingSearch.SetCard(second, "Assignment Prompts");
                group.Children.Add(second);

                var opened = new List<FoldPart>();
                var en = CultureInfo.GetCultureInfo("en-US");
                Assert.Equal(1, SettingSearch.Filter(group, SettingSearch.Words("vibrate"), en, opened));
                Assert.Equal(Visibility.Visible, first.Visibility);
                Assert.Equal(Visibility.Collapsed, low.Visibility);
                Assert.Equal(Visibility.Collapsed, below.Visibility);
                Assert.Equal(Visibility.Visible, vibrate.Visibility);
                Assert.Equal(Visibility.Collapsed, repeat.Visibility);
                Assert.Equal(Visibility.Visible, container.Visibility);
                Assert.True(more.IsExpanded);
                Assert.Equal(new FoldPart[] { more }, opened);
                Assert.False(CardFoldState.IsOpen("t.more"));
                Assert.Equal(0, changes);
                Assert.Equal(Visibility.Collapsed, second.Visibility);

                // The More row's hint names both inner rows, and is not the
                // rows' text: a word only the hint and one row share shows
                // that row alone.
                opened.Clear();
                Assert.Equal(1, SettingSearch.Filter(group, SettingSearch.Words("repeat"), en, opened));
                Assert.Equal(Visibility.Collapsed, vibrate.Visibility);
                Assert.Equal(Visibility.Visible, repeat.Visibility);

                // The group's name is every row's context.
                opened.Clear();
                Assert.Equal(5, SettingSearch.Filter(group, SettingSearch.Words("input"), en, opened));

                more.EndSearch();
                Assert.False(more.IsExpanded);
                Assert.Equal(0, changes);

                // A row its own rule hides is not counted, though it matches.
                vibrate.Visibility = Visibility.Collapsed;
                opened.Clear();
                Assert.Equal(0, SettingSearch.Filter(group, SettingSearch.Words("vibrate"), en, opened));
                Assert.Equal(Visibility.Collapsed, first.Visibility);
                Assert.Equal(Visibility.Collapsed, container.Visibility);
                Assert.Empty(opened);

                SettingSearch.Clear(group);
                Assert.Equal(Visibility.Visible, first.Visibility);
                Assert.Equal(Visibility.Visible, second.Visibility);
                Assert.Equal(Visibility.Visible, low.Visibility);
                Assert.Equal(Visibility.Visible, container.Visibility);
                Assert.Equal(Visibility.Collapsed, vibrate.Visibility);
            }
            finally
            {
                CardFoldState.Changed -= counted;
                CardFoldState.Load(null);
            }
        });
    }
}
