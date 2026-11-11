using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Controls;
using PadForge.Engine.Common.OpenXr;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The Extras page (#574): the Dashboard's ten service cards as a catalog
    /// of tiles and a page for each. These hold that every control came over
    /// as it was, that each page shows first what it was decided to show and
    /// keeps the rest under More, and that the three catalog switches with
    /// several switches behind them turn their own on and off as decided.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ExtrasPageTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        internal static string RepoText(params string[] parts) =>
            File.ReadAllText(System.IO.Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private static string Page() => RepoText("PadForge.App", "Views", "ExtrasPage.xaml");

        private static int Count(string text, string needle) =>
            text.Split(new[] { needle }, StringSplitOptions.None).Length - 1;

        /// <summary>One extra's page, from its panel to the next page's.</summary>
        private static string PageOf(string key)
        {
            string page = Page();
            var keys = PadForge.Views.ExtrasPage.Keys.ToList();
            int from = page.IndexOf("<StackPanel x:Name=\"" + key + "Page\"", StringComparison.Ordinal);
            Assert.True(from > 0, key + " has no page");
            int next = keys.IndexOf(key) + 1;
            int to = next < keys.Count
                ? page.IndexOf("<StackPanel x:Name=\"" + keys[next] + "Page\"", StringComparison.Ordinal)
                : page.Length;
            Assert.True(to > from, key + "'s page does not end");
            return page.Substring(from, to - from);
        }

        // ── every control came over ────────────────────────────────────

        /// <summary>The AutomationIds and names the ten cards carried on the
        /// Dashboard at 7aee616d. Each is on the Extras page once, and none
        /// is left on the Dashboard. Tests and UI scripts find a control by
        /// these, so a control that lost its id in the move is a control
        /// they can no longer find.</summary>
        [Fact]
        public void EveryMovedControl_KeepsItsIdAndItsName()
        {
            string page = Page();
            string dashboard = RepoText("PadForge.App", "Views", "DashboardPage.xaml");
            foreach (string id in new[]
            {
                "EnableWebControllerCheckBox", "Reset_DashboardViewModel_EnableWebController_1488",
                "EnableWebControllerPlainHttpCheckBox", "Reset_DashboardViewModel_EnableWebControllerPlainHttp_1608",
                "WebControllerPlainHttpLocalOnlyCheckBox", "Reset_DashboardViewModel_WebControllerPlainHttpLocalOnly_1615",
                "WebControllerAccessCodeBox", "NewWebAccessCodeButton",
                "EnableRemoteLinkCheckBox", "Reset_DashboardViewModel_EnableRemoteLink_1578",
                "Reset_DashboardViewModel_AutoReconnect_1581", "Reset_SettingsViewModel_IdentityProtectionModeIndex_1638",
                "Reset_RemoteLinkTrustedPeer_Name_1678", "Reset_RemoteLinkTrustedPeer_AllowRemoteAssignments_1685",
                "Reset_DashboardViewModel_EnableDsuMotionServer_1896", "Reset_DashboardViewModel_EnableGameModServer_2230",
                "Reset_DashboardViewModel_GameModDsxStandIn_2246", "Reset_DashboardViewModel_EnableGameTelemetry_2300",
                "Reset_DashboardViewModel_EnableTwitchChat_2245", "Reset_DashboardViewModel_TwitchChatClaimApprovalIndex_2289",
                "Reset_DashboardViewModel_EnableMenuOverlay_2023", "Reset_DashboardViewModel_EnableShiftLayerFlyout_2026",
                "Reset_DashboardViewModel_EnableProfileOverlay_2029", "Reset_DashboardViewModel_EnableTouchpadOverlay_2045",
                "ScreenOverlay_Add",
            })
            {
                string needle = "AutomationProperties.AutomationId=\"" + id + "\"";
                Assert.True(Count(page, needle) == 1, id + " is on the Extras page " + Count(page, needle) + " times");
                Assert.DoesNotContain(needle, dashboard);
            }
            // The Screen Overlay rows name their controls by the overlay's number.
            foreach (string bound in new[] { "EnableId", "OpacityId", "ChooseId", "ResetPositionId", "RemoveId" })
                Assert.Equal(1, Count(page, "AutomationProperties.AutomationId=\"{Binding " + bound + "}\""));
            foreach (string name in new[]
            {
                "HeadTrackingUdpCheckBox", "HeadTrackingEnabledReset", "HeadTrackingFreeTrackCheckBox", "HeadTrackingFreeTrackReset",
                "HeadTrackingOpenXrCheckBox", "HeadTrackingOpenXrReset", "HeadTrackingOpenXrRuntimeCombo", "HeadTrackingOpenXrRuntimeReset",
                "HeadTrackingUdpPortControls", "GameTelemetryCheckBox", "GameTelemetryGameCombo", "GameTelemetryFormatsButton",
                "TwitchChatCheckBox",
            })
            {
                Assert.True(Count(page, "x:Name=\"" + name + "\"") == 1, name + " is not on the Extras page once");
                Assert.DoesNotContain("x:Name=\"" + name + "\"", dashboard);
            }
            // The 43 reset buttons of the cards, and the two beside the
            // switches of Head Tracking and Overlays.
            Assert.Equal(45, Count(page, "<reset:SettingResetButton "));
            Assert.Equal(0, Count(dashboard, "<reset:SettingResetButton "));
            // The format editor's button kept its handler, which moved with it.
            Assert.Contains("Click=\"GameTelemetryFormatsButton_Click\"", page);
            Assert.Contains("private void GameTelemetryFormatsButton_Click(", RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs"));
            Assert.DoesNotContain("GameTelemetryFormatsButton_Click", RepoText("PadForge.App", "Views", "DashboardPage.xaml.cs"));
        }

        // ── what shows first, and what More holds ──────────────────────

        public static IEnumerable<object[]> FirstAndMore() => new[]
        {
            new object[] { "WebController", "Extras_More_WebController",
                new[] { "{Binding WebControllerStatus}", "{Binding WebControllerHttpsWarning}", "{Binding WebControllerQr}", "{Binding Dashboard_WebFooter," },
                new[] { "{Binding WebControllerPort,", "{Binding EnableWebControllerPlainHttp}", "{Binding WebControllerPlainHttpLocalOnly}",
                        "{Binding WebControllerPlainHttpPort,", "WebControllerAccessCodeBox", "NewWebAccessCodeButton", "{Binding WebControllerPlainQr}" } },
            new object[] { "RemoteLink", "Extras_More_RemoteLink",
                new[] { "{Binding RemoteLinkStatus}", "{Binding RemoteLink.TrustedPeers}", "{Binding RemoteLink.NearbyUnpaired}",
                        "{Binding RemoteLinkMyCode,", "{Binding RemoteLinkNatWarning}", "{Binding Dashboard_RemoteLinkFooter," },
                new[] { "{Binding AutoReconnect}", "{Binding RemoteLinkPort,", "{Binding RemoteLink.IdentityProtectionModeIndex,", "{Binding RemoteLinkConnectHost," } },
            new object[] { "HeadTracking", "Extras_More_HeadTracking",
                new[] { "{Binding HeadTrackingStatus}", "{Binding HeadTrackingRecenterCommand}", "IsChecked=\"{Binding HeadTrackingEnabled}\"",
                        "IsChecked=\"{Binding HeadTrackingFreeTrack}\"", "IsChecked=\"{Binding HeadTrackingOpenXr}\"",
                        "{Binding HeadTrackingRotationRange,", "{Binding HeadTrackingTranslationRange,", "{Binding Dashboard_HeadTrackingFooter," },
                new[] { "{Binding SelectedOpenXrRuntime,", "{Binding HeadTrackingUdpPort,", "{Binding HeadTrackingRangeYaw,", "{Binding HeadTrackingRangeZ," } },
            new object[] { "MotionServer", "Dashboard_Port",
                new[] { "{Binding DsuServerStatus}", "{Binding Dashboard_DsuFooter," },
                new[] { "{Binding DsuMotionServerPort," } },
            new object[] { "GameMods", "Extras_More_GameMods",
                new[] { "{Binding GameModServerStatus}", "{Binding Dashboard_GameModsFooter," },
                new[] { "{Binding GameModServerPort,", "{Binding GameModDsxStandIn}" } },
            new object[] { "GameTelemetry", "Extras_More_GameTelemetry",
                new[] { "{Binding GameTelemetryStatus}", "{Binding SelectedGameTelemetryOption,", "{Binding Dashboard_GameTelemetryFooter," },
                new[] { "{Binding GameTelemetryPort,", "{Binding GameTelemetryForwardPort,", "{Binding GameTelemetryFullSpeed,", "GameTelemetryFormatsButton_Click" } },
        };

        /// <summary>A page with a More row shows its status and the one thing
        /// to do first, and keeps its ports and its rarely changed settings
        /// under More. The More row is the last thing in the card, it is
        /// stored under the extra's own key, and its hint names what it
        /// holds.</summary>
        [Theory]
        [MemberData(nameof(FirstAndMore))]
        public void APage_ShowsItsStatusFirst_AndKeepsTheRestUnderMore(string key, string hint, string[] first, string[] more)
        {
            string body = PageOf(key);
            string open = "<controls:MoreRow x:Name=\"" + key + "More\" StateKey=\"Extras." + key + ".More\"";
            int at = body.IndexOf(open, StringComparison.Ordinal);
            Assert.True(at > 0, key + " has no More row of its own key");
            int end = body.IndexOf("</controls:MoreRow>", at, StringComparison.Ordinal);
            Assert.True(end > at);
            string shown = body.Substring(0, at), kept = body.Substring(at, end - at);

            Assert.Contains("AutomationProperties.AutomationId=\"Extras_More_" + key + "\"", kept);
            Assert.Contains("Header=\"{Binding Common_More, Source={x:Static strings:Strings.Instance}}\"", kept);
            Assert.Contains("Hint=\"{Binding " + hint + ", Source={x:Static strings:Strings.Instance}}\"", kept);
            foreach (string needle in first)
            {
                Assert.True(shown.Contains(needle, StringComparison.Ordinal), key + " does not show " + needle + " first");
                Assert.False(kept.Contains(needle, StringComparison.Ordinal), key + " also keeps " + needle + " under More");
            }
            foreach (string needle in more)
            {
                Assert.True(kept.Contains(needle, StringComparison.Ordinal), key + " does not keep " + needle + " under More");
                Assert.False(shown.Contains(needle, StringComparison.Ordinal), key + " also shows " + needle + " first");
            }
            // Nothing follows the More row but the card's closing tags (and
            // the comment that heads the next page).
            string after = Regex.Replace(body.Substring(end + "</controls:MoreRow>".Length), "<!--.*?-->", "", RegexOptions.Singleline);
            Assert.Equal("</StackPanel></Border></StackPanel>", Regex.Replace(after, @"\s+", ""));
        }

        /// <summary>One label column to a card. Where a card shows boxes
        /// first and keeps more of them under More, they sit in two grids,
        /// and one shared size group on both label columns, inside one scope
        /// on the card, starts every box at the same x. No number box or
        /// picker is left in a row of its own, where it would start at its
        /// own label's width: the Web Controller's main port and Head
        /// Tracking's OpenXR runtime did, on the Dashboard and on the first
        /// build of these pages.</summary>
        [Theory]
        [InlineData("WebController", "WebControllerLabel")]
        [InlineData("HeadTracking", "HeadTrackingLabel")]
        [InlineData("GameTelemetry", "GameTelemetryLabel")]
        public void ACardWithBoxesFirstAndUnderMore_KeepsOneLabelColumn(string key, string group)
        {
            string body = PageOf(key);
            Assert.Equal(1, Count(body, "<StackPanel Grid.IsSharedSizeScope=\"True\">"));
            Assert.Equal(2, Count(body, "<ColumnDefinition Width=\"Auto\" SharedSizeGroup=\"" + group + "\"/>"));

            System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var doc = System.Xml.Linq.XDocument.Parse(Page());
            var page = Assert.Single(doc.Descendants(), e => (string)e.Attribute(x + "Name") == key + "Page");
            var boxes = page.Descendants().Where(e => e.Name.LocalName is "NumberBox" or "ComboBox").ToList();
            Assert.True(boxes.Count >= 2, key + " has no boxes to line up");
            foreach (var box in boxes)
            {
                // A box is a grid's own cell, or sits in the panel that is.
                var cell = box.Parent!.Name.LocalName == "Grid" ? box : box.Parent;
                var grid = cell.Parent!;
                string what = (string)box.Attribute("Value") ?? (string)box.Attribute("SelectedItem") ?? box.ToString();
                Assert.True(grid.Name.LocalName == "Grid", "not in a grid: " + what);
                Assert.Equal("1", (string)cell.Attribute("Grid.Column"));
                var label = grid.Elements().First(e => e.Name.LocalName == "Grid.ColumnDefinitions").Elements().First();
                Assert.True((string)label.Attribute("SharedSizeGroup") == group, "its grid's label column is not in the shared group: " + what);
            }
        }

        /// <summary>The rule over this PC's code on the Remote Link page
        /// shows only while the code or the network verdict does. On the
        /// Dashboard the connect-by-address row followed it. That row sits
        /// under More now, so with no code yet the rule stood over nothing
        /// but the card's closing note.</summary>
        [Fact]
        public void TheRemoteLinkPage_DrawsNoRuleOverNothing()
        {
            string body = PageOf("RemoteLink");
            int code = body.IndexOf("<Condition Binding=\"{Binding HasRemoteLinkMyCode}\" Value=\"False\"/>", StringComparison.Ordinal);
            int verdict = body.IndexOf("<Condition Binding=\"{Binding HasRemoteLinkNatWarning}\" Value=\"False\"/>", StringComparison.Ordinal);
            int shown = body.IndexOf("<StackPanel Visibility=\"{Binding HasRemoteLinkMyCode,", StringComparison.Ordinal);
            Assert.True(code > 0 && verdict > code && shown > verdict, "the rule's two conditions sit over the code's panel");
            Assert.Contains("<Setter Property=\"Visibility\" Value=\"Collapsed\"/>", body.Substring(verdict, shown - verdict));
            int rule = body.LastIndexOf("<Border Height=\"1\"", code, StringComparison.Ordinal);
            Assert.True(rule > 0 && !body.Substring(rule, code - rule).Contains("/>", StringComparison.Ordinal), "the conditions belong to the rule");
        }

        /// <summary>The four pages with few settings show them all: no More
        /// row with one line under it.</summary>
        [Theory]
        [InlineData("TwitchChat")]
        [InlineData("Overlays")]
        [InlineData("TouchpadOverlay")]
        [InlineData("ScreenOverlay")]
        public void AShortPage_HasNoMoreRow(string key) =>
            Assert.DoesNotContain("<controls:MoreRow", PageOf(key));

        /// <summary>A page opens on its title with the extra's own switch
        /// beside it, drawn as a switch that says On or Off, with the
        /// switch's reset button. The switch is the card's own check box,
        /// with the binding and the id it always had. Screen Overlay has a
        /// switch for each overlay and none of its own.</summary>
        [Theory]
        [InlineData("WebController", "EnableWebController", "CommandParameter=\"EnableWebController\"")]
        [InlineData("RemoteLink", "EnableRemoteLink", "CommandParameter=\"EnableRemoteLink\"")]
        [InlineData("HeadTracking", "HeadTrackingAnyEnabled", "Command=\"{Binding ResetHeadTrackingSourcesCommand}\"")]
        [InlineData("MotionServer", "EnableDsuMotionServer", "CommandParameter=\"EnableDsuMotionServer\"")]
        [InlineData("GameMods", "EnableGameModServer", "CommandParameter=\"EnableGameModServer\"")]
        [InlineData("GameTelemetry", "EnableGameTelemetry", "CommandParameter=\"EnableGameTelemetry\"")]
        [InlineData("TwitchChat", "EnableTwitchChat", "CommandParameter=\"EnableTwitchChat\"")]
        [InlineData("Overlays", "OverlaysAnyEnabled", "Command=\"{Binding ResetOverlaysCommand}\"")]
        [InlineData("TouchpadOverlay", "EnableTouchpadOverlay", "CommandParameter=\"EnableTouchpadOverlay\"")]
        public void APage_OpensOnItsTitleAndItsOwnSwitch(string key, string property, string reset)
        {
            string body = PageOf(key);
            int card = body.IndexOf("<Border Style=\"{StaticResource CardBorder}\"", StringComparison.Ordinal);
            Assert.True(card > 0);
            string header = body.Substring(0, card);

            Assert.Contains("x:Name=\"" + key + "Title\" AutomationProperties.HeadingLevel=\"Level1\"", header);
            Assert.Equal(1, Count(header, "<CheckBox "));
            Assert.Contains("<CheckBox Style=\"{StaticResource SwitchCheckBox}\" controls:SwitchLook.ShowState=\"True\"", header);
            Assert.Contains("IsChecked=\"{Binding " + property + "}\"", header);
            // A switch's name for a screen reader is its Content, which it does not draw.
            Assert.Matches(new Regex("Content=\"\\{Binding \\w+, Source=\\{x:Static strings:Strings.Instance\\}\\}\""), header);
            Assert.Equal(1, Count(header, "<reset:SettingResetButton "));
            Assert.Contains(reset, header);
            // The catalog's tile works the same setting.
            Assert.Contains("x:Name=\"" + key + "Tile\"", Page());
            Assert.Matches(new Regex("x:Name=\"" + key + "Tile\"[^>]*IsOn=\"\\{Binding " + property + "\\}\"", RegexOptions.Singleline), Page());
        }

        [Fact]
        public void TheScreenOverlayPage_HasASwitchForEachOverlay_AndTheTileTurnsThemAllOnOrOff()
        {
            string body = PageOf("ScreenOverlay");
            string header = body.Substring(0, body.IndexOf("<Border Style=\"{StaticResource CardBorder}\"", StringComparison.Ordinal));
            Assert.DoesNotContain("<CheckBox", header);
            Assert.Contains("IsChecked=\"{Binding Enabled}\"", body);
            Assert.Matches(new Regex("x:Name=\"ScreenOverlayTile\"[^>]*IsOn=\"\\{Binding ScreenOverlaysAnyEnabled\\}\"", RegexOptions.Singleline), Page());
        }

        // ── the page's code and the window ─────────────────────────────

        [Fact]
        public void ThePageShowsOnePanelAtATime_AndTheWindowOpensItOnTheCatalog()
        {
            string code = RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
            Assert.Contains("CatalogView.Visibility = key == null ? Visibility.Visible : Visibility.Collapsed;", code);
            Assert.Contains("page.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;", code);
            Assert.Contains("PageScroll.ScrollToTop();", code);
            // A tile opens its page, the way back and the Back keys return to the catalog.
            Assert.Contains("controls:ExtraTile.OpenRequested=\"Tile_OpenRequested\"", Page());
            Assert.Contains("private void Back_Click(object sender, RoutedEventArgs e) => ShowCatalog();", code);
            // Alt+Left, the keyboard's Back key and the mouse's Back button.
            // Never Backspace, which a read-only address box does not take.
            Assert.Contains("new KeyGesture(Key.Left, ModifierKeys.Alt), new KeyGesture(Key.BrowserBack)", code);
            Assert.Contains("CommandBindings.Add(new CommandBinding(BackCommand,", code);
            Assert.DoesNotContain("CommandBinding(NavigationCommands.BrowseBack", code);
            Assert.Contains("MouseButton.XButton1", code);
            Assert.Equal(10, Count(Page(), "Click=\"Back_Click\""));

            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("<views:ExtrasPage x:Name=\"ExtrasPageView\" Visibility=\"Collapsed\"/>", RepoText("PadForge.App", "MainWindow.xaml"));
            Assert.Contains("ExtrasPageView.DataContext = _viewModel.Dashboard;", window);
            // Coming from another page lands on the catalog, and so does a
            // click on the Extras item. Any other click in the sidebar
            // leaves an open extra's page alone.
            int navigate = window.IndexOf("private void NavigateToTag(string tag)", StringComparison.Ordinal);
            int arrive = window.IndexOf("if (tag == \"Extras\" && ExtrasPageView.Visibility != Visibility.Visible) ExtrasPageView.ShowCatalog();", StringComparison.Ordinal);
            int show = window.IndexOf("ExtrasPageView.Visibility = tag == \"Extras\" ? Visibility.Visible : Visibility.Collapsed;", StringComparison.Ordinal);
            Assert.True(navigate > 0 && arrive > navigate && show > arrive, "the page is checked for hidden before it is shown");
            int activate = window.IndexOf("private void ActivateNavItem(NavigationViewItem nvi)", StringComparison.Ordinal);
            int click = window.IndexOf("if (tag == \"Extras\") ExtrasPageView.ShowCatalog();", activate, StringComparison.Ordinal);
            Assert.True(activate > 0 && click > activate && click < navigate, "a click on the Extras item goes back to the catalog");
            Assert.Equal(2, Count(window, "ExtrasPageView.ShowCatalog();"));
            // The sidebar item, after Devices and ahead of the controllers.
            int devices = window.IndexOf("NavView.MenuItems.Add(_navDevices);", StringComparison.Ordinal);
            int extras = window.IndexOf("NavView.MenuItems.Add(_navExtras);", StringComparison.Ordinal);
            int controllers = window.IndexOf("RebuildControllerSection();", extras, StringComparison.Ordinal);
            Assert.True(devices > 0 && extras > devices && controllers > extras);
            Assert.Contains("SetNavAutomationId(_navExtras, \"Extras\");", window);
            Assert.Contains("private const int ControllerInsertIndex = 4;", window);
            Assert.Contains("if (_navExtras != null) _navExtras.Content = Strings.Instance.Extras_Title;", window);
            // The Welcome Tour's stop on the services points at the item now.
            Assert.Contains("(_navExtras, Strings.Instance.Extras_Title, Strings.Instance.Tour_Extras_Body),", window);
            Assert.DoesNotContain("ServicesSection", window);
        }

        // ── the three switches with several behind them ────────────────

        /// <summary>Head tracking's setters write the engine's switches, so
        /// each test puts them back.</summary>
        private static void WithHeadTracking(Action<DashboardViewModel> body)
        {
            bool udp = HeadTrackingRuntime.Enabled, free = HeadTrackingRuntime.FreeTrackEnabled, xr = HeadTrackingRuntime.OpenXrEnabled;
            try
            {
                var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => Array.Empty<OpenXrRuntimeEntry>() };
                vm.HeadTrackingEnabled = false;
                vm.HeadTrackingFreeTrack = false;
                vm.HeadTrackingOpenXr = false;
                body(vm);
            }
            finally
            {
                HeadTrackingRuntime.Enabled = udp;
                HeadTrackingRuntime.FreeTrackEnabled = free;
                HeadTrackingRuntime.OpenXrEnabled = xr;
            }
        }

        [Fact]
        public void TheHeadTrackingSwitch_IsOnWhileAnyInputIs_AndBringsBackTheOnesItTurnedOff() => WithHeadTracking(vm =>
        {
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.False(vm.HeadTrackingAnyEnabled);

            // The first time, with nothing remembered: the UDP listener and
            // the FreeTrack reader. The headset input waits to be chosen.
            vm.HeadTrackingAnyEnabled = true;
            Assert.True(vm.HeadTrackingEnabled);
            Assert.True(vm.HeadTrackingFreeTrack);
            Assert.False(vm.HeadTrackingOpenXr);
            Assert.True(vm.HeadTrackingAnyEnabled);
            Assert.Contains(nameof(DashboardViewModel.HeadTrackingAnyEnabled), changed);
            Assert.Equal(0, vm.HeadTrackingLastSources);

            // The user's own choice on the page: the headset alone.
            vm.HeadTrackingEnabled = false;
            vm.HeadTrackingFreeTrack = false;
            vm.HeadTrackingOpenXr = true;
            Assert.True(vm.HeadTrackingAnyEnabled);
            Assert.Equal(0, vm.HeadTrackingLastSources);

            // Off turns every input off and remembers the one that was on.
            changed.Clear();
            vm.HeadTrackingAnyEnabled = false;
            Assert.False(vm.HeadTrackingEnabled || vm.HeadTrackingFreeTrack || vm.HeadTrackingOpenXr);
            Assert.False(HeadTrackingRuntime.OpenXrEnabled);
            Assert.Equal(DashboardViewModel.HeadTrackingOpenXrBit, vm.HeadTrackingLastSources);
            Assert.Contains(nameof(DashboardViewModel.HeadTrackingAnyEnabled), changed);
            Assert.Contains(nameof(DashboardViewModel.HeadTrackingLastSources), changed);

            // On brings back that one and no other.
            vm.HeadTrackingAnyEnabled = true;
            Assert.False(vm.HeadTrackingEnabled);
            Assert.False(vm.HeadTrackingFreeTrack);
            Assert.True(vm.HeadTrackingOpenXr);

            // Setting it to what it already is changes nothing.
            changed.Clear();
            vm.HeadTrackingAnyEnabled = true;
            Assert.Empty(changed);

            // The reset beside it puts each input at its default, off.
            vm.ResetHeadTrackingSourcesCommand.Execute(null);
            Assert.False(vm.HeadTrackingAnyEnabled);
        });

        /// <summary>A profile or a settings load sets the three inputs one
        /// after another. A set remembered from the steps in between would
        /// be one nobody chose, so only the switch itself remembers.</summary>
        [Fact]
        public void TheHeadTrackingSwitch_RemembersOnlyWhatItTurnedOffItself() => WithHeadTracking(vm =>
        {
            vm.HeadTrackingLastSources = DashboardViewModel.HeadTrackingUdpBit;
            vm.HeadTrackingEnabled = true;
            vm.HeadTrackingFreeTrack = true;
            vm.HeadTrackingOpenXr = true;
            // All three go off one at a time, as a profile applies them.
            vm.HeadTrackingEnabled = false;
            vm.HeadTrackingFreeTrack = false;
            vm.HeadTrackingOpenXr = false;
            Assert.Equal(DashboardViewModel.HeadTrackingUdpBit, vm.HeadTrackingLastSources);
            vm.HeadTrackingAnyEnabled = true;
            Assert.True(vm.HeadTrackingEnabled);
            Assert.False(vm.HeadTrackingFreeTrack);
            Assert.False(vm.HeadTrackingOpenXr);
            // A value outside the three bits is cut to them.
            vm.HeadTrackingLastSources = 0xFF;
            Assert.Equal(7, vm.HeadTrackingLastSources);
        });

        [Fact]
        public void TheOverlaysSwitch_IsOnWhileAnyOverlayIs_AndBringsBackTheOnesItTurnedOff()
        {
            var vm = new DashboardViewModel();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            // A new file shows all three.
            Assert.True(vm.OverlaysAnyEnabled);

            vm.EnableShiftLayerFlyout = false;
            Assert.True(vm.OverlaysAnyEnabled);
            Assert.Contains(nameof(DashboardViewModel.OverlaysAnyEnabled), changed);

            vm.OverlaysAnyEnabled = false;
            Assert.False(vm.EnableMenuOverlay || vm.EnableShiftLayerFlyout || vm.EnableProfileOverlay);
            Assert.False(vm.OverlaysAnyEnabled);
            Assert.Equal(DashboardViewModel.MenuOverlayBit | DashboardViewModel.ProfileOverlayBit, vm.OverlaysLastShown);

            vm.OverlaysAnyEnabled = true;
            Assert.True(vm.EnableMenuOverlay);
            Assert.False(vm.EnableShiftLayerFlyout);
            Assert.True(vm.EnableProfileOverlay);

            // With nothing remembered, on shows all three.
            var fresh = new DashboardViewModel { EnableMenuOverlay = false, EnableShiftLayerFlyout = false, EnableProfileOverlay = false };
            Assert.False(fresh.OverlaysAnyEnabled);
            Assert.Equal(0, fresh.OverlaysLastShown);
            fresh.OverlaysAnyEnabled = true;
            Assert.True(fresh.EnableMenuOverlay && fresh.EnableShiftLayerFlyout && fresh.EnableProfileOverlay);

            // The reset beside it puts each overlay at its default, on.
            vm.OverlaysAnyEnabled = false;
            vm.ResetOverlaysCommand.Execute(null);
            Assert.True(vm.EnableMenuOverlay && vm.EnableShiftLayerFlyout && vm.EnableProfileOverlay);
        }

        [Fact]
        public void TheScreenOverlaySwitch_IsOnWhileAnyOverlayIs_AndBringsBackTheOnesItTurnedOff()
        {
            var vm = new DashboardViewModel();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.Single(vm.ScreenOverlays);
            Assert.False(vm.ScreenOverlaysAnyEnabled);

            // With nothing remembered, on turns the first overlay on.
            vm.ScreenOverlaysAnyEnabled = true;
            Assert.True(vm.ScreenOverlays[0].Enabled);
            Assert.True(vm.ScreenOverlaysAnyEnabled);
            Assert.Contains(nameof(DashboardViewModel.ScreenOverlaysAnyEnabled), changed);

            // Three overlays, the second and third on.
            vm.AddScreenOverlay();
            vm.AddScreenOverlay();
            Assert.Equal(new[] { 1, 2, 3 }, vm.ScreenOverlays.Select(o => o.Number));
            vm.ScreenOverlays[0].Enabled = false;
            vm.ScreenOverlays[1].Enabled = true;
            vm.ScreenOverlays[2].Enabled = true;

            vm.ScreenOverlaysAnyEnabled = false;
            Assert.DoesNotContain(vm.ScreenOverlays, o => o.Enabled);
            Assert.Equal(0b110, vm.ScreenOverlaysLastOn);

            vm.ScreenOverlaysAnyEnabled = true;
            Assert.Equal(new[] { false, true, true }, vm.ScreenOverlays.Select(o => o.Enabled));

            // The remembered overlays were removed since: the first one left.
            vm.ScreenOverlaysAnyEnabled = false;
            vm.RemoveScreenOverlay(vm.ScreenOverlays[2]);
            vm.RemoveScreenOverlay(vm.ScreenOverlays[1]);
            Assert.Equal(new[] { 1 }, vm.ScreenOverlays.Select(o => o.Number));
            vm.ScreenOverlaysAnyEnabled = true;
            Assert.True(vm.ScreenOverlays[0].Enabled);
        }

        // ── the settings file ──────────────────────────────────────────

        [Fact]
        public void TheSettingsFile_KeepsWhatTheSwitchesTurnedOff_AndWritesNothingUntilOneHas()
        {
            var serializer = new XmlSerializer(typeof(AppSettingsData));

            var none = new StringWriter();
            serializer.Serialize(none, new AppSettingsData());
            foreach (string element in new[] { "HeadTrackingLastSources", "OverlaysLastShown", "ScreenOverlaysLastOn" })
                Assert.DoesNotContain(element, none.ToString(), StringComparison.Ordinal);

            var some = new StringWriter();
            serializer.Serialize(some, new AppSettingsData { HeadTrackingLastSources = 5, OverlaysLastShown = 3, ScreenOverlaysLastOn = 130 });
            var back = (AppSettingsData)serializer.Deserialize(new StringReader(some.ToString()));
            Assert.Equal(5, back.HeadTrackingLastSources);
            Assert.Equal(3, back.OverlaysLastShown);
            Assert.Equal(130, back.ScreenOverlaysLastOn);

            // The load, the save and the mark that the file changed. A field
            // the builder drops is the failure a round trip of the data
            // class alone cannot see.
            string service = RepoText("PadForge.App", "Services", "SettingsService.cs");
            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            foreach (string name in new[] { "HeadTrackingLastSources", "OverlaysLastShown", "ScreenOverlaysLastOn" })
            {
                Assert.Contains("_mainVm.Dashboard." + name + " = appSettings." + name + ";", service, StringComparison.Ordinal);
                Assert.Contains(name + " = _mainVm.Dashboard." + name + ",", service, StringComparison.Ordinal);
                Assert.Contains("or nameof(DashboardViewModel." + name + ")", window, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// The two parts the Extras page is drawn with (#574), built from the
    /// markup the app ships: a catalog tile, and the check box drawn as a
    /// switch. They read On and Off from the string table, so they run with
    /// the tests that change the language.
    /// </summary>
    [Collection("CultureSwitching")]
    public class ExtrasPartsTests
    {
        /// <summary>WPF elements need an STA thread.</summary>
        private static void RunSta(Action body)
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
            Assert.True(thread.Join(30000), "the Extras part test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>The Extras styles of Resources/ControllerIcons.xaml as
        /// they ship, with stand-ins for the resources they take from the
        /// rest of the app. A template that does not parse, or a binding of
        /// the wrong type, fails here and not when the page first shows.</summary>
        private static ResourceDictionary ShippedStyles()
        {
            string file = ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            int from = file.IndexOf("<Style x:Key=\"SwitchCheckBox\"", StringComparison.Ordinal);
            int to = file.LastIndexOf("</Style>", StringComparison.Ordinal) + "</Style>".Length;
            Assert.True(from > 0 && to > from);
            string assembly = typeof(ExtraTile).Assembly.GetName().Name;
            string markup =
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                + " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\""
                + " xmlns:controls=\"clr-namespace:PadForge.Controls;assembly=" + assembly + "\""
                + " xmlns:strings=\"clr-namespace:PadForge.Resources.Strings;assembly=" + assembly + "\">"
                + "<FontFamily x:Key=\"TelemetryFontFamily\">Consolas</FontFamily>"
                + "<DropShadowEffect x:Key=\"NeutralHoverGlow\"/>"
                + "<Style x:Key=\"CardBorder\" TargetType=\"Border\"/>"
                + "<Style x:Key=\"CardTitle\" TargetType=\"TextBlock\"/>"
                + "<Style x:Key=\"CardDescription\" TargetType=\"TextBlock\"/>"
                + file.Substring(from, to - from)
                + "</ResourceDictionary>";
            return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(markup);
        }

        [Fact]
        public void ATile_BuildsFromItsShippedTemplate_OpensItsPage_AndItsSwitchWorksTheSetting() => RunSta(() =>
        {
            var styles = ShippedStyles();
            var tile = new ExtraTile
            {
                Key = "WebController",
                Glyph = "\uE774",
                Title = "Web Controller",
                Line = "Use a phone or tablet as a controller.",
                SwitchLabel = "Enable Web Controller Server",
                Status = "Stopped",
                Resources = styles,
            };
            tile.Style = (Style)styles[typeof(ExtraTile)];
            Assert.True(tile.ApplyTemplate(), "the tile has no template");
            Assert.False(tile.Focusable);

            var open = Assert.IsType<Button>(tile.Template.FindName(ExtraTile.OpenPartName, tile));
            var toggle = Assert.IsType<CheckBox>(tile.Template.FindName(ExtraTile.SwitchPartName, tile));
            var word = Assert.IsType<TextBlock>(tile.Template.FindName("StateWord", tile));
            var status = Assert.IsType<TextBlock>(tile.Template.FindName("StatusText", tile));

            // A UI script and a screen reader find the two by these.
            Assert.Equal("Extras_Open_WebController", AutomationProperties.GetAutomationId(open));
            Assert.Equal("Extras_Switch_WebController", AutomationProperties.GetAutomationId(toggle));
            Assert.Equal("Web Controller", AutomationProperties.GetName(open));
            Assert.Equal("Use a phone or tablet as a controller.", AutomationProperties.GetHelpText(open));
            Assert.Equal("Stopped", AutomationProperties.GetItemStatus(open));
            Assert.Equal("Enable Web Controller Server", toggle.Content);

            // Off: the word, and no status under it.
            Assert.False(tile.IsOn);
            Assert.False(toggle.IsChecked);
            Assert.Equal(Strings.Instance.Common_Off, word.Text);
            Assert.Equal(Visibility.Collapsed, status.Visibility);

            // The switch writes the setting, and the setting moves the switch.
            toggle.IsChecked = true;
            Assert.True(tile.IsOn);
            Assert.Equal(Strings.Instance.Common_On, word.Text);
            Assert.Equal(Visibility.Visible, status.Visibility);
            Assert.Equal("Stopped", status.Text);
            tile.IsOn = false;
            Assert.False(toggle.IsChecked);
            Assert.Equal(Strings.Instance.Common_Off, word.Text);

            // An extra with no status line shows only the word.
            tile.IsOn = true;
            tile.Status = "";
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            tile.Status = null;
            Assert.Equal(Visibility.Collapsed, status.Visibility);

            // The face asks for the page. The switch never does.
            var opened = new List<object>();
            tile.OpenRequested += (_, e) => opened.Add(e.OriginalSource);
            open.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Same(tile, Assert.Single(opened));
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Single(opened);

            // A key set after the template names the parts too.
            tile.Key = "RemoteLink";
            Assert.Equal("Extras_Open_RemoteLink", AutomationProperties.GetAutomationId(open));
            Assert.Equal("Extras_Switch_RemoteLink", AutomationProperties.GetAutomationId(toggle));
        });

        [Fact]
        public void ASwitch_IsACheckBoxThatSaysOnOrOff_WhenAskedTo() => RunSta(() =>
        {
            var styles = ShippedStyles();
            var box = new CheckBox { Content = "Enable Remote Link Server", Resources = styles, Style = (Style)styles["SwitchCheckBox"] };
            Assert.True(box.ApplyTemplate(), "the switch has no template");
            var word = Assert.IsType<TextBlock>(box.Template.FindName("StateText", box));
            var knob = Assert.IsType<Ellipse>(box.Template.FindName("Knob", box));

            // A tile's switch draws the track alone.
            Assert.Equal(Visibility.Collapsed, word.Visibility);
            // A page's switch writes the word before it.
            SwitchLook.SetShowState(box, true);
            Assert.Equal(Visibility.Visible, word.Visibility);
            Assert.Equal(Strings.Instance.Common_Off, word.Text);
            Assert.Equal(HorizontalAlignment.Left, knob.HorizontalAlignment);

            box.IsChecked = true;
            Assert.Equal(Strings.Instance.Common_On, word.Text);
            Assert.Equal(HorizontalAlignment.Right, knob.HorizontalAlignment);
            Assert.NotEqual(Strings.Instance.Common_On, Strings.Instance.Common_Off);

            // It stays a check box to a screen reader, named by the Content it does not draw.
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(box);
            Assert.Equal(System.Windows.Automation.Peers.AutomationControlType.CheckBox, peer.GetAutomationControlType());
            Assert.Equal("Enable Remote Link Server", peer.GetName());
        });
    }
}
