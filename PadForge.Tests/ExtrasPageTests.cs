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
            string dashboard = RepoText("PadForge.App", "Views", "HomePage.xaml");
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
            Assert.DoesNotContain("GameTelemetryFormatsButton_Click", RepoText("PadForge.App", "Views", "HomePage.xaml.cs"));
        }

        // ── what shows first, and what More holds ──────────────────────

        public static IEnumerable<object[]> FirstAndMore() => new[]
        {
            new object[] { "WebController", "Extras_More_WebController",
                new[] { "{Binding WebControllerStatus}", "{Binding WebControllerHttpsWarning}", "{Binding WebControllerQr}",
                        "{Binding Extras_WebController_Step1,", "{Binding WebControllerPhones}" },
                new[] { "{Binding WebControllerPort,", "{Binding EnableWebControllerPlainHttp}", "{Binding WebControllerPlainHttpLocalOnly}",
                        "{Binding WebControllerPlainHttpPort,", "WebControllerAccessCodeBox", "NewWebAccessCodeButton", "{Binding WebControllerPlainQr}" } },
            new object[] { "RemoteLink", "Extras_More_RemoteLink",
                new[] { "{Binding RemoteLinkStatus}", "{Binding RemoteLink.TrustedPeers}", "{Binding RemoteLink.NearbyUnpaired}",
                        "{Binding RemoteLinkMyCode,", "{Binding RemoteLinkNatWarning}" },
                new[] { "{Binding AutoReconnect}", "{Binding RemoteLinkPort,", "{Binding RemoteLink.IdentityProtectionModeIndex,", "{Binding RemoteLinkConnectHost," } },
            new object[] { "HeadTracking", null,
                new[] { "{Binding HeadTrackingStatus}", "{Binding HeadTrackingRecenterCommand}", "IsChecked=\"{Binding HeadTrackingEnabled}\"",
                        "IsChecked=\"{Binding HeadTrackingFreeTrack}\"", "IsChecked=\"{Binding HeadTrackingOpenXr}\"",
                        "{Binding HeadTrackingRotationRange,", "{Binding HeadTrackingTranslationRange,", "{Binding Dashboard_HeadTrackingFooter," },
                new[] { "{Binding SelectedOpenXrRuntime,", "{Binding HeadTrackingUdpPort,", "{Binding HeadTrackingRangeYaw,", "{Binding HeadTrackingRangeZ," } },
            new object[] { "MotionServer", "Dashboard_Port",
                new[] { "{Binding DsuServerStatus}", "{Binding Extras_StatusHint_MotionServer,", "{Binding Dashboard_DsuFooter," },
                new[] { "{Binding DsuMotionServerPort," } },
            new object[] { "GameMods", "Extras_More_GameMods",
                new[] { "{Binding GameModServerStatus}", "{Binding Extras_StatusHint_GameMods,", "{Binding Dashboard_GameModsFooter," },
                new[] { "{Binding GameModServerPort,", "{Binding GameModDsxStandIn}" } },
            new object[] { "GameTelemetry", "Extras_More_GameTelemetry",
                new[] { "{Binding GameTelemetryStatus}", "{Binding Extras_StatusHint_GameTelemetry,", "{Binding SelectedGameTelemetryOption," },
                new[] { "{Binding GameTelemetryPort,", "{Binding GameTelemetryForwardPort,", "{Binding GameTelemetryFullSpeed,",
                        "{Binding Dashboard_GameTelemetryFooter,", "GameTelemetryFormatsButton_Click" } },
        };

        /// <summary>A page with a More card shows its status and the one
        /// thing to do first, and keeps its ports and its rarely changed
        /// settings under More. The More row is a card of its own (the
        /// MoreCardRow in a FoldList), it is stored under the extra's own
        /// key, and its hint names what it holds. Only the page's note may
        /// follow it.</summary>
        [Theory]
        [MemberData(nameof(FirstAndMore))]
        public void APage_ShowsItsStatusFirst_AndKeepsTheRestUnderMore(string key, string hint, string[] first, string[] more)
        {
            string body = PageOf(key);
            string open = "<controls:MoreRow x:Name=\"" + key + "More\" Style=\"{StaticResource MoreCardRow}\" StateKey=\"Extras." + key + ".More\"";
            int at = body.IndexOf(open, StringComparison.Ordinal);
            Assert.True(at > 0, key + " has no More card of its own key");
            Assert.EndsWith("<controls:FoldList Gap=\"0\" Margin=\"0,16,0,0\">", Regex.Replace(body.Substring(0, at), @"\s+$", ""));
            int end = body.IndexOf("</controls:MoreRow>", at, StringComparison.Ordinal);
            Assert.True(end > at);
            string shown = body.Substring(0, at), kept = body.Substring(at, end - at);

            Assert.Contains("AutomationProperties.AutomationId=\"Extras_More_" + key + "\"", kept);
            Assert.Contains("Header=\"{Binding Common_More, Source={x:Static strings:Strings.Instance}}\"", kept);
            if (hint != null)
                Assert.Contains("Hint=\"{Binding " + hint + ", Source={x:Static strings:Strings.Instance}}\"", kept);
            else
            {
                // Head Tracking's hint names its three settings in their own words.
                int port = kept.IndexOf("<Binding Path=\"Dashboard_HeadTrackingPort\" Source=\"{x:Static strings:Strings.Instance}\"/>", StringComparison.Ordinal);
                int runtime = kept.IndexOf("<Binding Path=\"Dashboard_HeadTrackingOpenXrRuntime\" Source=\"{x:Static strings:Strings.Instance}\"/>", StringComparison.Ordinal);
                int axes = kept.IndexOf("<Binding Path=\"Dashboard_HeadTrackingAxisRanges\" Source=\"{x:Static strings:Strings.Instance}\"/>", StringComparison.Ordinal);
                Assert.True(kept.Contains("<MultiBinding StringFormat=\"{}{0}, {1}, {2}\">", StringComparison.Ordinal) && port > 0 && runtime > port && axes > runtime,
                    "Head Tracking's More hint is not its three settings' names in order");
                Assert.DoesNotContain("Hint=\"{Binding", kept);
            }
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
            // The More card is the page's last card: after it come only its
            // FoldList's end, the page's note when it has one, and the page's
            // end (and the comment that heads the next page).
            string after = Regex.Replace(body.Substring(end + "</controls:MoreRow>".Length), "<!--.*?-->", "", RegexOptions.Singleline);
            after = Regex.Replace(after, @"\s+", "");
            Assert.StartsWith("</controls:FoldList>", after);
            Assert.Matches(new Regex("^(<TextBlock[^>]*ExtraNote[^>]*/>|<TextBlock[^>]*ExtraNote[^>]*>.*</TextBlock>)?</StackPanel>$", RegexOptions.Singleline),
                after.Substring("</controls:FoldList>".Length));
        }

        /// <summary>The label column each page's board draws: 260 on most,
        /// 300 on Head Tracking, 230 on the Web Controller, 200 on Twitch
        /// Chat. The page's DzLabel is 260, and a page with another width
        /// shadows it in its panel's resources with the same look
        /// (ExtraLabel). Every row that starts with a label starts with the
        /// one DzLabel draws, so every control of a page starts at one x, and
        /// every box and picker stands in a row.</summary>
        [Theory]
        [InlineData("WebController", 230)]
        [InlineData("HeadTracking", 300)]
        [InlineData("GameTelemetry", 260)]
        [InlineData("TwitchChat", 200)]
        [InlineData("RemoteLink", 260)]
        [InlineData("MotionServer", 260)]
        [InlineData("GameMods", 260)]
        [InlineData("Overlays", 260)]
        [InlineData("TouchpadOverlay", 260)]
        [InlineData("ScreenOverlay", 260)]
        public void EachPage_KeepsOneLabelColumn_AtItsBoardsWidth(string key, int width)
        {
            System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var doc = System.Xml.Linq.XDocument.Parse(Page());
            System.Xml.Linq.XElement Setter(System.Xml.Linq.XElement style, string property) =>
                style.Elements().SingleOrDefault(e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == property);

            // The page's own column, and the look every column shares.
            var resources = doc.Root!.Elements().Single(e => e.Name.LocalName == "UserControl.Resources");
            var label = Assert.Single(resources.Elements(), e => (string)e.Attribute(x + "Key") == "DzLabel");
            Assert.Equal("{StaticResource ExtraLabel}", (string)label.Attribute("BasedOn"));
            Assert.Equal("260", (string)Setter(label, "Width")!.Attribute("Value"));
            var look = Assert.Single(resources.Elements(), e => (string)e.Attribute(x + "Key") == "ExtraLabel");
            Assert.Equal("14", (string)Setter(look, "FontSize")!.Attribute("Value"));
            Assert.Equal("0,0,12,0", (string)Setter(look, "Margin")!.Attribute("Value"));

            var page = Assert.Single(doc.Descendants(), e => (string)e.Attribute(x + "Name") == key + "Page");
            var own = page.Elements().SingleOrDefault(e => e.Name.LocalName == "StackPanel.Resources")?
                .Elements().Where(e => (string)e.Attribute(x + "Key") == "DzLabel").ToList() ?? new List<System.Xml.Linq.XElement>();
            if (width == 260)
                Assert.Empty(own);
            else
            {
                var shadow = Assert.Single(own);
                Assert.Equal("{StaticResource ExtraLabel}", (string)shadow.Attribute("BasedOn"));
                Assert.Equal(width.ToString(System.Globalization.CultureInfo.InvariantCulture), (string)Setter(shadow, "Width")!.Attribute("Value"));
            }

            var rows = page.Descendants().Where(e => e.Name.LocalName == "SettingRow").ToList();
            if (key != "Overlays") Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                var head = row.Elements().First();
                if (head.Name.LocalName != "TextBlock") continue;
                Assert.Equal("{StaticResource DzLabel}", (string)head.Attribute("Style"));
                Assert.Null(head.Attribute("Width"));
            }
            // A box or a picker stands in a row after its label. A paired PC's
            // name is typed over in its own line, under its own name.
            foreach (var box in page.Descendants().Where(e => e.Name.LocalName is "NumberBox" or "ComboBox" or "TextBox"))
            {
                if ((string)box.Attribute(x + "Name") == "PeerNameBox") continue;
                Assert.True(box.Ancestors().Any(a => a.Name.LocalName == "SettingRow"),
                    key + ": not in a row: " + ((string)box.Attribute("Value") ?? (string)box.Attribute("Text") ?? (string)box.Attribute("SelectedItem") ?? (string)box.Attribute("SelectedIndex")));
            }
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

        /// <summary>A page's head: what stands before its first card, its
        /// status card, its row of cards or its More card.</summary>
        private static string HeaderOf(string body)
        {
            int cut = body.Length;
            foreach (string marker in new[]
            {
                "Style=\"{StaticResource CardBorder}\"", "Style=\"{StaticResource ExtraStatusCard}\"",
                "Style=\"{StaticResource ExtraCard}\"", "<controls:FoldList", "<controls:FlexLinePanel", "<ItemsControl",
            })
            {
                int at = body.IndexOf(marker, StringComparison.Ordinal);
                if (at > 0) cut = Math.Min(cut, at);
            }
            Assert.True(cut < body.Length, "the page has no card");
            return body.Substring(0, cut);
        }

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
            string header = HeaderOf(body);

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
            string header = HeaderOf(body);
            Assert.DoesNotContain("<CheckBox", header);
            // Add Overlay stands where another page's switch does.
            Assert.Contains("<ui:Button AutomationProperties.AutomationId=\"ScreenOverlay_Add\"", header);
            Assert.Contains("IsChecked=\"{Binding Enabled}\"", body);
            Assert.Matches(new Regex("x:Name=\"ScreenOverlayTile\"[^>]*IsOn=\"\\{Binding ScreenOverlaysAnyEnabled\\}\"", RegexOptions.Singleline), Page());
        }

        // ── the catalog, the frame and the notes ───────────────────────

        public static IEnumerable<object[]> EveryKey() => PadForge.Views.ExtrasPage.Keys.Select(k => new object[] { k });

        private static readonly System.Xml.Linq.XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static System.Xml.Linq.XElement Named(System.Xml.Linq.XDocument doc, string name) =>
            Assert.Single(doc.Descendants(), e => (string)e.Attribute(XamlNs + "Name") == name);

        /// <summary>The catalog as its board of the world-class pass draws
        /// it: the boards' gutters, the title of 28 in the display face
        /// with its line, how many extras are on at the head's end, three
        /// group headings 26 under what is above them, and each group's
        /// tiles sharing their row, in the decided order.</summary>
        [Fact]
        public void TheCatalog_IsTheBoardsCatalog()
        {
            string text = Page();
            var doc = System.Xml.Linq.XDocument.Parse(text);
            var catalog = Named(doc, "CatalogView");
            // 36 at the sides and the foot, 26 over the title, on the
            // catalog and on every extra's own page.
            Assert.Equal("36,26,36,36", (string)catalog.Parent.Attribute("Margin"));
            Assert.Null(catalog.Attribute("Margin"));
            // A tile is lit only while the engine runs, as on Home.
            Assert.Equal("{Binding IsEngineOn}", catalog.Attributes().Single(a => a.Name.LocalName == "Heat.IsLive").Value);
            var parts = catalog.Elements().ToList();

            var head = parts[0];
            Assert.Equal("CatalogHead", (string)head.Attribute(XamlNs + "Name"));
            Assert.Equal("FlexLinePanel", head.Name.LocalName);
            var titles = head.Elements().First().Elements().ToList();
            Assert.Equal("{StaticResource ExtraTitle}", (string)titles[0].Attribute("Style"));
            Assert.Equal("Level1", (string)titles[0].Attribute("AutomationProperties.HeadingLevel"));
            Assert.Equal("{Binding Extras_Title, Source={x:Static strings:Strings.Instance}}", (string)titles[0].Attribute("Text"));
            Assert.Equal("{StaticResource ExtraLine}", (string)titles[1].Attribute("Style"));
            Assert.Equal("{Binding Extras_Subtitle, Source={x:Static strings:Strings.Instance}}", (string)titles[1].Attribute("Text"));
            // The pages' title: 28 in the display face, on a line of 40.6.
            var titleStyle = Assert.Single(doc.Descendants(), e => (string)e.Attribute(XamlNs + "Key") == "ExtraTitle");
            string Set(string property) => (string)titleStyle.Elements().Single(s => (string)s.Attribute("Property") == property).Attribute("Value");
            Assert.Equal("28", Set("FontSize"));
            Assert.Equal("{StaticResource DisplayFontFamily}", Set("FontFamily"));
            Assert.Equal("40.6", Set("LineHeight"));

            // 2 of 10 on: the number large, in the code face and ember, and
            // the words around it as the language puts them, all three from
            // the view model that counts the switches. The runs stand on
            // one line of markup so no space falls between them.
            var count = Named(doc, "OnCountText");
            Assert.Same(head, count.Parent);
            Assert.Equal("Bottom", (string)count.Attribute("VerticalAlignment"));
            Assert.Equal("Extras_OnCount", (string)count.Attribute("AutomationProperties.AutomationId"));
            Assert.StartsWith("{Binding ExtrasOnSpoken,", (string)count.Attribute("AutomationProperties.Name"));
            var runs = count.Elements().ToList();
            Assert.Equal(new[] { "{Binding ExtrasOnLead, Mode=OneWay}", "{Binding ExtrasOnNumber, Mode=OneWay}", "{Binding ExtrasOnTail, Mode=OneWay}" },
                runs.Select(r => (string)r.Attribute("Text")));
            Assert.Equal("20", (string)runs[1].Attribute("FontSize"));
            Assert.Equal("{StaticResource TelemetryFontFamily}", (string)runs[1].Attribute("FontFamily"));
            Assert.Equal("{DynamicResource EmberHotBrush}", (string)runs[1].Attribute("Foreground"));
            Assert.Contains("\"><Run Text=\"{Binding ExtrasOnLead, Mode=OneWay}\"/><Run Text=\"{Binding ExtrasOnNumber, Mode=OneWay}\"", text);
            Assert.Contains("/><Run Text=\"{Binding ExtrasOnTail, Mode=OneWay}\"/></TextBlock>", text);

            Assert.DoesNotContain("&#xE74C;", text);
            Assert.DoesNotContain("UpperConverter", text);
            Assert.DoesNotContain(catalog.Descendants(), e => e.Name.LocalName == "WrapPanel");

            var headings = parts.Where(e => (string)e.Attribute("Style") == "{StaticResource PageSectionTitle}").ToList();
            Assert.Equal(new[] { "Extras_Group_Play", "Extras_Group_Send", "Extras_Group_Screen" },
                headings.Select(h => Regex.Match((string)h.Attribute("Text"), @"\{Binding (\w+),").Groups[1].Value));
            Assert.All(headings, h => Assert.Equal("0,26,0,12", (string)h.Attribute("Margin")));
            var grids = parts.Where(e => e.Name.LocalName == "TileGridPanel").ToList();
            Assert.Equal(new[] { 4, 3, 3 }, grids.Select(g => g.Elements().Count()));
            // Tiles at least 240 wide, 14 apart, sharing their row's whole
            // width, and two and two where four do not fit one row.
            Assert.All(grids, g =>
            {
                Assert.Equal("240", (string)g.Attribute("MinItemWidth"));
                Assert.Equal("14", (string)g.Attribute("Gap"));
                Assert.Equal("True", (string)g.Attribute("FillRows"));
                Assert.Equal("True", (string)g.Attribute("Balance"));
            });
            for (int i = 0; i < 3; i++) Assert.Same(grids[i], headings[i].ElementsAfterSelf().First());
            var tiles = grids.SelectMany(g => g.Elements()).ToList();
            Assert.Equal(PadForge.Views.ExtrasPage.Keys, tiles.Select(t => (string)t.Attribute("Key")));
            // Show QR on the Web Controller's tile alone, in Home's words.
            var web = Named(doc, "WebControllerTile");
            Assert.Equal("{Binding Home_ShowQr, Source={x:Static strings:Strings.Instance}}", (string)web.Attribute("ActionText"));
            Assert.All(tiles.Where(t => t != web), t => Assert.Null(t.Attribute("ActionText")));
        }

        /// <summary>Every page in the one frame its board draws: the way
        /// back, then a head with the extra's square (the tile's glyph, warm
        /// while the tile's setting is on), the page's name and its line.
        /// No drawing on a page glows.</summary>
        [Theory]
        [MemberData(nameof(EveryKey))]
        public void EachPage_HasTheFrame(string key)
        {
            var doc = System.Xml.Linq.XDocument.Parse(Page());
            var page = Named(doc, key + "Page");
            var parts = page.Elements().Where(e => e.Name.LocalName != "StackPanel.Resources").ToList();

            var back = Assert.Single(parts[0].Elements(), e => e.Name.LocalName == "Button");
            Assert.Equal("{StaticResource ExtrasCrumb}", (string)back.Attribute("Style"));
            Assert.Equal("Back_Click", (string)back.Attribute("Click"));
            Assert.Equal("Extras_Back_" + key, (string)back.Attribute("AutomationProperties.AutomationId"));
            Assert.Equal(new[] { "{StaticResource ExtrasCrumbText}", "{StaticResource ExtrasCrumbText}" },
                parts[0].Elements().Skip(1).Select(e => (string)e.Attribute("Style")));

            var head = parts[1];
            Assert.Equal("HeadRowPanel", head.Name.LocalName);
            Assert.Equal("0,10,0,0", (string)head.Attribute("Margin"));
            var leading = head.Elements().First();
            var badge = Assert.Single(leading.Elements(), e => e.Name.LocalName == "ExtraBadge");
            var tile = Named(doc, key + "Tile");
            Assert.Equal((string)tile.Attribute("Glyph"), (string)badge.Attribute("Glyph"));
            Assert.Equal((string)tile.Attribute("IsOn"), (string)badge.Attribute("IsOn"));
            var title = Assert.Single(leading.Descendants(), e => (string)e.Attribute(XamlNs + "Name") == key + "Title");
            Assert.Equal("{StaticResource ExtraTitle}", (string)title.Attribute("Style"));
            Assert.Equal("{StaticResource ExtraLine}", (string)title.ElementsAfterSelf().Single().Attribute("Style"));

            Assert.DoesNotContain(page.Descendants(), e => e.Name.LocalName is "DropShadowEffect" || e.Name.LocalName.EndsWith(".Effect", StringComparison.Ordinal));
            Assert.DoesNotContain(page.Descendants(), e => (string)e.Attribute("Property") == "Effect");
        }

        /// <summary>A note's bracketed words are a link: the note's string
        /// is bound three times, its parts around the brackets, with the
        /// middle one in a Hyperlink that opens the page it names. Every
        /// language that has the note keeps one pair of brackets in
        /// it.</summary>
        [Theory]
        [InlineData("HeadTracking", "Extras_Note_HeadTracking", "DevicesLink_Click")]
        [InlineData("GameTelemetry", "Extras_Note_GameTelemetry", "DevicesLink_Click")]
        [InlineData("Overlays", "Extras_Note_Overlays", "MenusLink_Click")]
        public void TheNotes_LinkTheBracketedWords(string key, string note, string handler)
        {
            var doc = System.Xml.Linq.XDocument.Parse(Page());
            var page = Named(doc, key + "Page");
            var block = Assert.Single(page.Elements(), e => e.Name.LocalName == "TextBlock" && (string)e.Attribute("Style") == "{StaticResource ExtraNote}");
            var inlines = block.Elements().ToList();
            Assert.Equal(new[] { "Run", "Hyperlink", "Run" }, inlines.Select(i => i.Name.LocalName));
            string Part(int n) => "{Binding " + note + ", Source={x:Static strings:Strings.Instance}, Converter={StaticResource BracketPartConverter}, ConverterParameter=" + n + ", Mode=OneWay}";
            Assert.Equal(Part(0), (string)inlines[0].Attribute("Text"));
            Assert.Equal(Part(1), (string)Assert.Single(inlines[1].Elements()).Attribute("Text"));
            Assert.Equal(Part(2), (string)inlines[2].Attribute("Text"));
            Assert.Equal(handler, (string)inlines[1].Attribute("Click"));
            Assert.Equal("Extras_Link_" + key, (string)inlines[1].Attribute("AutomationProperties.AutomationId"));
            Assert.Contains("private void " + handler + "(object sender, RoutedEventArgs e)", RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs"));

            int checkedFiles = 0;
            foreach (string file in Directory.GetFiles(System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings"), "Strings*.resx"))
            {
                var m = Regex.Match(File.ReadAllText(file), "<data name=\"" + note + "\"[^>]*>\\s*<value>(.*?)</value>", RegexOptions.Singleline);
                if (!m.Success) continue;
                checkedFiles++;
                string v = m.Groups[1].Value;
                int open = v.IndexOf('['), close = v.IndexOf(']');
                Assert.True(open >= 0 && close > open && v.IndexOf('[', open + 1) < 0 && v.IndexOf(']', close + 1) < 0,
                    System.IO.Path.GetFileName(file) + " has no single pair of brackets in " + note + ": " + v);
            }
            Assert.True(checkedFiles >= 1, "no string table has " + note);
        }

        /// <summary>The Range card's Reset All puts the rotation and
        /// translation ranges back and leaves the per-axis ranges under More
        /// alone, which keep resets of their own.</summary>
        [Fact]
        public void TheRangeResetAll_ResetsTheTwoRanges()
        {
            int rotation = HeadTrackingRuntime.RotationRangeDeg, translation = HeadTrackingRuntime.TranslationRangeCm;
            int yaw = HeadTrackingRuntime.GetAxisRangeOverride(0);
            try
            {
                WithHeadTracking(vm =>
                {
                    vm.HeadTrackingRotationRange = 45;
                    vm.HeadTrackingTranslationRange = 99;
                    vm.HeadTrackingRangeYaw = 30;
                    vm.ResetHeadTrackingRangesCommand.Execute(null);
                    Assert.Equal(HeadTrackingRuntime.DefaultRotationRangeDeg, vm.HeadTrackingRotationRange);
                    Assert.Equal(HeadTrackingRuntime.DefaultTranslationRangeCm, vm.HeadTrackingTranslationRange);
                    Assert.Equal(30, vm.HeadTrackingRangeYaw);
                });
            }
            finally
            {
                HeadTrackingRuntime.RotationRangeDeg = rotation;
                HeadTrackingRuntime.TranslationRangeCm = translation;
                HeadTrackingRuntime.SetAxisRange(0, yaw);
            }
        }

        /// <summary>The status flames of Head Tracking and Game Telemetry burn
        /// while data arrives: each tick sets whether it does with the status
        /// line, and off when the extra stops.</summary>
        [Fact]
        public void TheFlames_ReadWhetherDataArrives()
        {
            string service = RepoText("PadForge.App", "Services", "InputService.cs").Replace("\r", "");
            int head = service.IndexOf("private void UpdateHeadTrackingStatus()", StringComparison.Ordinal);
            Assert.True(head > 0);
            string body = service.Substring(head, service.IndexOf("\n        }\n", head, StringComparison.Ordinal) - head);
            int off = body.IndexOf("dash.IsHeadTrackingReceiving = false;", StringComparison.Ordinal);
            int stopped = body.IndexOf("if (_headTrackingStatusVersion == HeadTrackingStatusStopped)", StringComparison.Ordinal);
            int status = body.IndexOf("dash.HeadTrackingStatus = BuildHeadTrackerStatus(ht);", StringComparison.Ordinal);
            int on = body.IndexOf("dash.IsHeadTrackingReceiving = ht.Source != PadForge.Common.Input.HeadTrackerSource.None;", StringComparison.Ordinal);
            Assert.True(off > 0 && stopped > off && status > stopped && on > status, "Head Tracking's flame is not set with its status");

            string telemetry = RepoText("PadForge.App", "Services", "InputService.GameTelemetry.cs").Replace("\r", "");
            Assert.Contains("            if (row == null)\n            {\n                dash.IsGameTelemetryReceiving = false;", telemetry);
            Assert.DoesNotContain("IsGameTelemetryRunning", telemetry);
            int line = telemetry.IndexOf("dash.GameTelemetryStatus = BuildGameTelemetryStatus(status, row.Format);", StringComparison.Ordinal);
            int live = telemetry.IndexOf("dash.IsGameTelemetryReceiving = status.Source != TelemetrySource.None;", StringComparison.Ordinal);
            Assert.True(line > 0 && live > line, "Game Telemetry's flame is not set with its status");

            // The pages read them.
            Assert.Contains("IsLive=\"{Binding IsHeadTrackingReceiving}\"", PageOf("HeadTracking"));
            Assert.Contains("IsLive=\"{Binding IsGameTelemetryReceiving}\"", PageOf("GameTelemetry"));
        }

        /// <summary>The notes' links reach their pages: Devices, and the first
        /// controller's Macros &amp; Menus with the menus showing, or Home
        /// with no controller.</summary>
        [Fact]
        public void TheLinks_ReachTheirPages()
        {
            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            int home = window.IndexOf("HomePageView.OpenExtrasRequested += (s, e) => SelectNavItemByTag(\"Extras\");", StringComparison.Ordinal);
            int devices = window.IndexOf("ExtrasPageView.OpenDevicesRequested += (s, e) => SelectNavItemByTag(\"Devices\");", StringComparison.Ordinal);
            int menus = window.IndexOf("ExtrasPageView.OpenMenusRequested += (s, e) => OpenFirstControllerMenus();", StringComparison.Ordinal);
            Assert.True(home > 0 && devices > home && menus > devices, "the links are not wired beside Home's");
            int method = window.IndexOf("private void OpenFirstControllerMenus()", StringComparison.Ordinal);
            Assert.True(method > 0);
            string body = window.Substring(method, window.IndexOf("NavigateToSlot(first.PadIndex);", method, StringComparison.Ordinal) - method);
            Assert.Contains("_viewModel.NavControllerItems.FirstOrDefault()", body);
            Assert.Contains("SelectNavItemByTag(\"Home\");", body);
            Assert.Contains(".SelectedConfigTab = PadViewModel.MenusTabIndex;", body);

            string code = RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
            Assert.Contains("=> OpenDevicesRequested?.Invoke(this, EventArgs.Empty);", code);
            Assert.Contains("=> OpenMenusRequested?.Invoke(this, EventArgs.Empty);", code);
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
            // The sidebar item, after Devices and Profiles and ahead of the
            // Controllers label and the controllers under it (HomePageTests
            // pins the whole order).
            int devices = window.IndexOf("NavView.MenuItems.Add(_navDevices);", StringComparison.Ordinal);
            int extras = window.IndexOf("NavView.MenuItems.Add(_navExtras);", StringComparison.Ordinal);
            int controllers = window.IndexOf("RebuildControllerSection();", extras, StringComparison.Ordinal);
            Assert.True(devices > 0 && extras > devices && controllers > extras);
            Assert.Contains("SetNavAutomationId(_navExtras, \"Extras\");", window);
            Assert.Contains("private const int ControllerInsertIndex = 5;", window);
            Assert.Contains("if (_navExtras != null) _navExtras.Content = Strings.Instance.Extras_Title;", window);
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

        /// <summary>The screenshot script frames an extra's page, and the
        /// catalog, by words the page shows: a card's row, a button, a
        /// tile. An anchor no string reads, or one the page stopped
        /// binding, would refuse its shot at the next capture and say so
        /// only then.</summary>
        [Fact]
        public void TheExtrasRecipesAnchorOnNamesThePagesShow()
        {
            string script = RepoText("tools", "capture_all.ps1").Replace("\r\n", "\n");
            var anchors = new List<string>();
            foreach (string recipe in new[]
            {
                "Capture-ExtrasPages", "Capture-ExtrasCatalog", "Capture-WebPlainSection", "Capture-GameModsCard",
                "Capture-GameTelemetryCard", "Capture-TwitchChatShots", "Capture-ScreenOverlayCard",
            })
            {
                int start = script.IndexOf("\nfunction " + recipe + " {\n", StringComparison.Ordinal);
                Assert.True(start >= 0, "no function " + recipe);
                string body = script.Substring(start, script.IndexOf("\n}\n", start, StringComparison.Ordinal) - start);
                foreach (string line in body.Split('\n'))
                {
                    // A list of anchors ends at its first quote followed by a bracket.
                    int list = line.IndexOf("Anchors = @(", StringComparison.Ordinal);
                    if (list < 0) list = line.IndexOf("-Anchors @(", StringComparison.Ordinal);
                    if (list >= 0)
                    {
                        string rest = line.Substring(list);
                        int end = rest.IndexOf("\")", StringComparison.Ordinal);
                        Assert.True(end > 0, line);
                        foreach (Match item in Regex.Matches(rest.Substring(0, end + 1), "\"(?<a>[^\"]+)\""))
                            anchors.Add(item.Groups["a"].Value);
                    }
                    foreach (Match shown in Regex.Matches(line, "Test-InFrame \"(?<a>[^\"]+)\""))
                        anchors.Add(shown.Groups["a"].Value);
                }
            }
            // Positive control: the scan read the recipes' anchors.
            Assert.True(anchors.Count >= 13, "only " + anchors.Count + " anchors read: " + string.Join(" | ", anchors));

            var english = System.Xml.Linq.XDocument.Parse(RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"))
                .Root.Elements("data").ToLookup(d => (string)d.Element("value"), d => (string)d.Attribute("name"));
            string page = RepoText("PadForge.App", "Views", "ExtrasPage.xaml") + RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
            foreach (string written in anchors.Distinct())
            {
                string anchor = written.Replace("$([char]0x2026)", "…").Replace("$([char]0x2019)", "’");
                var keys = english[anchor].ToList();
                Assert.True(keys.Count > 0, "no string reads '" + anchor + "'");
                Assert.True(keys.Any(k => Regex.IsMatch(page, @"(?<![\w])" + Regex.Escape(k) + @"(?![\w])")),
                    "the Extras page shows no '" + anchor + "' (" + string.Join(", ", keys) + ")");
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
                + " xmlns:conv=\"clr-namespace:PadForge.Converters;assembly=" + assembly + "\""
                + " xmlns:strings=\"clr-namespace:PadForge.Resources.Strings;assembly=" + assembly + "\">"
                + "<FontFamily x:Key=\"TelemetryFontFamily\">Consolas</FontFamily>"
                + "<Style x:Key=\"CardBorder\" TargetType=\"Border\"/>"
                + "<Style x:Key=\"CardTitle\" TargetType=\"TextBlock\"/>"
                + "<Style x:Key=\"CardDescription\" TargetType=\"TextBlock\"/>"
                + string.Concat(StandInBrushes.Select((b, i) => "<SolidColorBrush x:Key=\"" + b + "\" Color=\"#FF0000" + (i + 1).ToString("X2") + "\"/>"))
                + "<Geometry x:Key=\"FlameOuterGeometry\">M12 3 C8 8 6 12 12 21 C18 12 16 8 12 3 Z</Geometry>"
                + file.Substring(from, to - from)
                + "</ResourceDictionary>";
            return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(markup);
        }

        /// <summary>Stand-ins for the theme brushes the parts color
        /// themselves with, each a color of its own, so a test can tell
        /// which one a part took.</summary>
        private static readonly string[] StandInBrushes =
        {
            "SteelRaisedBrush", "EmberWellBrush", "TextFillColorSecondaryBrush", "EmberHotBrush",
            "TextFillColorTertiaryBrush", "WaitBrush", "EmberBrush", "EmberEdgeBrush", "LinkHoverBrush",
            "TextFillColorPrimaryBrush", "SteelFieldBrush", "CardStrokeColorDefaultBrush", "EmberRingBrush",
            "EmberRingSoftBrush", "EmberTintBrush",
        };

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
            var action = Assert.IsType<Button>(tile.Template.FindName(ExtraTile.ActionPartName, tile));
            var word = Assert.IsType<TextBlock>(tile.Template.FindName("StateWord", tile));
            var status = Assert.IsType<TextBlock>(tile.Template.FindName("StatusText", tile));
            var title = Assert.IsType<TextBlock>(tile.Template.FindName("TitleText", tile));
            var line = Assert.IsType<TextBlock>(tile.Template.FindName("LineText", tile));
            var card = Assert.IsType<Border>(tile.Template.FindName("Card", tile));
            var wash = Assert.IsType<Border>(tile.Template.FindName("Wash", tile));
            var square = Assert.IsType<Border>(tile.Template.FindName("Square", tile));
            var light = Assert.IsType<Halo>(tile.Template.FindName("Light", tile));
            var squareLight = Assert.IsType<Halo>(tile.Template.FindName("SquareLight", tile));
            var dotLight = Assert.IsType<Halo>(tile.Template.FindName("DotLight", tile));
            var dot = Assert.IsType<Ellipse>(tile.Template.FindName("Dot", tile));

            // A UI script and a screen reader find the two by these.
            Assert.Equal("Extras_Open_WebController", AutomationProperties.GetAutomationId(open));
            Assert.Equal("Extras_Switch_WebController", AutomationProperties.GetAutomationId(toggle));
            Assert.Equal("Web Controller", AutomationProperties.GetName(open));
            Assert.Equal("Use a phone or tablet as a controller.", AutomationProperties.GetHelpText(open));
            Assert.Equal("Stopped", AutomationProperties.GetItemStatus(open));
            Assert.Equal("Enable Web Controller Server", toggle.Content);

            // The board's measures: a square of 44 and a name of 16, and
            // the lights its shadows come to (30 at 0.13 with a ring at
            // 0.16 around the card, 16 at 0.28 around the square, 8 at 0.9
            // around the dot).
            Assert.Equal(new Size(44, 44), new Size(square.Width, square.Height));
            Assert.Equal(16.0, title.FontSize);
            Assert.Equal((37.5, 0.065, 0.16), (light.Radius, light.Strength, light.Rim));
            Assert.Equal((20.0, 0.14), (squareLight.Radius, squareLight.Strength));
            Assert.Equal((10.0, 0.45), (dotLight.Radius, dotLight.Strength));

            // The face lies under the words and the square, which take no
            // click, so a click on them opens the page. The switch and the
            // action are controls of their own.
            foreach (UIElement part in new UIElement[] { title, line, word, status, squareLight, dotLight, light, wash })
                Assert.False(part.IsHitTestVisible);
            Assert.True(toggle.IsHitTestVisible && action.IsHitTestVisible && open.IsHitTestVisible);

            // The foot is one line: a status too long for the tile is cut
            // short, and the face's tooltip holds all of it while the
            // extra is on.
            Assert.Equal(TextTrimming.CharacterEllipsis, status.TextTrimming);
            Assert.Equal(TextWrapping.NoWrap, status.TextWrapping);

            // Off: the word alone, with no dot, no status and no light,
            // whatever the status holds.
            Assert.False(tile.IsOn);
            Assert.False(toggle.IsChecked);
            Assert.Equal(Strings.Instance.Common_Off, word.Text);
            Assert.Equal(Visibility.Visible, word.Visibility);
            Assert.Same(styles["TextFillColorTertiaryBrush"], word.Foreground);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            Assert.Equal(Visibility.Collapsed, dotLight.Visibility);
            Assert.False(light.IsLit || squareLight.IsLit || dotLight.IsLit);
            Assert.Equal(Visibility.Collapsed, wash.Visibility);
            Assert.Same(styles["SteelFieldBrush"], square.Background);
            Assert.Null(open.ToolTip);

            // On and live, which a tile is unless what holds it says
            // otherwise: the switch writes the setting, the status stands
            // in ember in place of the word, and the tile is lit.
            toggle.IsChecked = true;
            Assert.True(tile.IsOn);
            Assert.True(Heat.GetIsLive(tile));
            Assert.Equal(Visibility.Collapsed, word.Visibility);
            Assert.Equal(Visibility.Visible, status.Visibility);
            Assert.Equal("Stopped", status.Text);
            Assert.Same(styles["EmberHotBrush"], status.Foreground);
            Assert.Equal(FontWeights.SemiBold, status.FontWeight);
            Assert.Equal(Visibility.Visible, dotLight.Visibility);
            Assert.Same(styles["EmberBrush"], dot.Fill);
            Assert.True(light.IsLit && squareLight.IsLit && dotLight.IsLit);
            Assert.Equal(Visibility.Visible, wash.Visibility);
            Assert.Same(styles["EmberRingBrush"], card.BorderBrush);
            Assert.Same(styles["EmberTintBrush"], square.Background);
            Assert.Same(styles["EmberRingSoftBrush"], square.BorderBrush);
            Assert.Equal("Stopped", open.ToolTip);

            // On while the engine is stopped: the switch and the status
            // stay, in steel, and nothing glows.
            Heat.SetIsLive(tile, false);
            Assert.True(toggle.IsChecked);
            Assert.Equal(Visibility.Visible, status.Visibility);
            Assert.Same(styles["TextFillColorSecondaryBrush"], status.Foreground);
            Assert.Equal(FontWeights.Normal, status.FontWeight);
            Assert.Equal(Visibility.Visible, dotLight.Visibility);
            Assert.Same(styles["TextFillColorTertiaryBrush"], dot.Fill);
            Assert.False(light.IsLit || squareLight.IsLit || dotLight.IsLit);
            Assert.Equal(Visibility.Collapsed, wash.Visibility);
            Assert.NotSame(styles["EmberRingBrush"], card.BorderBrush);
            Assert.Same(styles["SteelFieldBrush"], square.Background);
            Heat.SetIsLive(tile, true);
            Assert.True(light.IsLit);

            // The setting moves the switch, and off is the word again.
            tile.IsOn = false;
            Assert.False(toggle.IsChecked);
            Assert.Equal(Strings.Instance.Common_Off, word.Text);
            Assert.Equal(Visibility.Visible, word.Visibility);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            Assert.Null(open.ToolTip);

            // An extra with no status line: the word On stands for it, in
            // ember, and the face has no tooltip.
            tile.IsOn = true;
            foreach (string none in new[] { "", null })
            {
                tile.Status = none;
                Assert.Equal(Visibility.Collapsed, status.Visibility);
                Assert.Equal(Visibility.Visible, word.Visibility);
                Assert.Equal(Strings.Instance.Common_On, word.Text);
                Assert.Same(styles["EmberHotBrush"], word.Foreground);
                Assert.Equal(FontWeights.SemiBold, word.FontWeight);
                Assert.Null(open.ToolTip);
            }
            tile.Status = "Stopped";
            Assert.Equal(Visibility.Visible, status.Visibility);

            // The action: there while the tile names one and the extra is
            // on and live, and nowhere else.
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            tile.ActionText = "Show QR";
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Show QR", action.Content);
            Assert.Equal("Extras_Open_WebController_Action", AutomationProperties.GetAutomationId(action));
            Heat.SetIsLive(tile, false);
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            Heat.SetIsLive(tile, true);
            tile.IsOn = false;
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            tile.IsOn = true;
            Assert.Equal(Visibility.Visible, action.Visibility);

            // The face asks for the page, and so does the action. The
            // switch never does.
            var opened = new List<object>();
            tile.OpenRequested += (_, e) => opened.Add(e.OriginalSource);
            open.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Same(tile, Assert.Single(opened));
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Single(opened);
            action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(2, opened.Count);
            tile.ActionText = null;
            Assert.Equal(Visibility.Collapsed, action.Visibility);

            // A key set after the template names the parts too.
            tile.Key = "RemoteLink";
            Assert.Equal("Extras_Open_RemoteLink", AutomationProperties.GetAutomationId(open));
            Assert.Equal("Extras_Switch_RemoteLink", AutomationProperties.GetAutomationId(toggle));
        });

        /// <summary>The tile's second face, the one Home shows for an extra
        /// that is on (the HomeExtraTile style): one button that asks for
        /// the extra's page, under an id that starts Home_Extra_ so it never
        /// shares one with the catalog's tile, the word On with the live
        /// status after it (the extra's one line while it reports none),
        /// and no switch. A tile that names a second button has one at its
        /// end, which asks for the page too.</summary>
        [Fact]
        public void HomesTile_BuildsFromItsShippedTemplate_OpensThePage_AndHasNoSwitch() => RunSta(() =>
        {
            var styles = ShippedStyles();
            var row = new ExtraTile
            {
                Key = "MotionServer",
                Glyph = "",
                Title = "Motion Server",
                Line = "Sends motion to emulators.",
                Status = "Listening on port 26760",
                IsOn = true,
                Resources = styles,
                Style = (Style)styles["HomeExtraTile"],
            };
            Assert.True(row.ApplyTemplate(), "Home's tile has no template");
            Assert.Equal(ExtraTile.HomeOpenIdPrefix, row.OpenPrefix);

            var open = Assert.IsType<Button>(row.Template.FindName(ExtraTile.OpenPartName, row));
            var action = Assert.IsType<Button>(row.Template.FindName(ExtraTile.ActionPartName, row));
            Assert.Null(row.Template.FindName(ExtraTile.SwitchPartName, row));
            Assert.Null(row.Template.FindName("StateWord", row));

            Assert.Equal("Home_Extra_MotionServer", AutomationProperties.GetAutomationId(open));
            Assert.Equal("Home_Extra_MotionServer_Action", AutomationProperties.GetAutomationId(action));
            Assert.Equal("Motion Server", AutomationProperties.GetName(open));
            Assert.Equal("Listening on port 26760", AutomationProperties.GetItemStatus(open));

            // What follows the word On: the status while there is one, and
            // the extra's one line otherwise.
            Assert.Equal("Listening on port 26760", row.Summary);
            row.Status = "";
            Assert.Equal("Sends motion to emulators.", row.Summary);
            row.Status = null;
            Assert.Equal("Sends motion to emulators.", row.Summary);
            row.Line = null;
            Assert.Equal(string.Empty, row.Summary);
            row.Status = "Listening on port 26760";
            // The line under the name is the format of the string table
            // over that summary, the one text in the face that wraps.
            var words = Descendants<TextBlock>(open).Single(t => t.TextWrapping == TextWrapping.Wrap);
            Assert.Equal(string.Format(Strings.Instance.Home_ExtraOn_Format, "Listening on port 26760"), words.Text);
            Assert.StartsWith(Strings.Instance.Common_On, words.Text);

            var opened = new List<object>();
            row.OpenRequested += (_, e) => opened.Add(e.OriginalSource);
            open.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Same(row, Assert.Single(opened));

            // The second button: gone while the tile names none, there once
            // it does, and a click on it asks for the page as the face does.
            Assert.Equal(Visibility.Collapsed, action.Visibility);
            row.ActionText = "Show QR";
            Assert.Equal(Visibility.Visible, action.Visibility);
            Assert.Equal("Show QR", action.Content);
            action.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(2, opened.Count);
            Assert.Same(row, opened[1]);
            row.ActionText = null;
            Assert.Equal(Visibility.Collapsed, action.Visibility);

            // The catalog's tile of the same extra keeps its own id.
            var tile = new ExtraTile { Key = "MotionServer", Resources = styles };
            tile.Style = (Style)styles[typeof(ExtraTile)];
            Assert.True(tile.ApplyTemplate());
            Assert.Equal("Extras_Open_MotionServer",
                AutomationProperties.GetAutomationId(Assert.IsType<Button>(tile.Template.FindName(ExtraTile.OpenPartName, tile))));
        });

        /// <summary>Every element of a type at or under a root, in the
        /// logical tree, which a template's content has before any layout.</summary>
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T hit) yield return hit;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject node)
                    foreach (T found in Descendants<T>(node))
                        yield return found;
        }

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

            // Off reads in the switch's own quiet color, On in the boards'
            // ember text.
            Assert.Same(styles["TextFillColorSecondaryBrush"], word.Foreground);

            box.IsChecked = true;
            Assert.Equal(Strings.Instance.Common_On, word.Text);
            Assert.Equal(HorizontalAlignment.Right, knob.HorizontalAlignment);
            Assert.NotEqual(Strings.Instance.Common_On, Strings.Instance.Common_Off);
            Assert.Same(styles["EmberHotBrush"], word.Foreground);

            // It stays a check box to a screen reader, named by the Content it does not draw.
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(box);
            Assert.Equal(System.Windows.Automation.Peers.AutomationControlType.CheckBox, peer.GetAutomationControlType());
            Assert.Equal("Enable Remote Link Server", peer.GetName());
        });

        /// <summary>The square at the head of an extra's page: the glyph on
        /// the raised steel while the extra is off, in the ember text color on
        /// the warm square while it is on. It only decorates, so it takes no
        /// focus.</summary>
        [Fact]
        public void AnExtraBadge_IsWarmWhileOn() => RunSta(() =>
        {
            var styles = ShippedStyles();
            var badge = new ExtraBadge { Glyph = "\uE77B", Resources = styles };
            badge.Style = (Style)styles[typeof(ExtraBadge)];
            Assert.True(badge.ApplyTemplate(), "the badge has no template");
            Assert.False(badge.Focusable);
            Assert.False(badge.IsTabStop);
            Assert.Equal(40, badge.Width);
            Assert.Equal(40, badge.Height);
            var well = Assert.IsType<Border>(badge.Template.FindName("Well", badge));
            var glyph = Assert.IsType<GlyphText>(badge.Template.FindName("Glyph", badge));
            Assert.Equal("\uE77B", glyph.Text);

            Assert.Same(styles["SteelRaisedBrush"], well.Background);
            Assert.Same(styles["TextFillColorSecondaryBrush"], glyph.Foreground);
            badge.IsOn = true;
            Assert.Same(styles["EmberWellBrush"], well.Background);
            Assert.Same(styles["EmberHotBrush"], glyph.Foreground);
            badge.IsOn = false;
            Assert.Same(styles["SteelRaisedBrush"], well.Background);
        });

        /// <summary>The flame of a status card has three states: an outline
        /// while the extra is off, gold while it is on and waits, ember while
        /// it works. The line beside it says the same, so it takes no
        /// focus.</summary>
        [Fact]
        public void AStatusFlame_HasThreeStates() => RunSta(() =>
        {
            var styles = ShippedStyles();
            var flame = new StatusFlame { Resources = styles };
            flame.Style = (Style)styles[typeof(StatusFlame)];
            Assert.True(flame.ApplyTemplate(), "the flame has no template");
            Assert.False(flame.Focusable);
            Assert.False(flame.IsTabStop);
            var path = Assert.IsType<System.Windows.Shapes.Path>(flame.Template.FindName("Flame", flame));
            Assert.Equal(FlowDirection.LeftToRight, path.FlowDirection);
            Assert.Same(styles["FlameOuterGeometry"], path.Data);

            // Off: an outline in the tertiary color, no fill.
            Assert.Equal(System.Windows.Media.Colors.Transparent, Assert.IsType<System.Windows.Media.SolidColorBrush>(path.Fill).Color);
            Assert.Same(styles["TextFillColorTertiaryBrush"], path.Stroke);
            // On and waiting: gold.
            flame.IsOn = true;
            Assert.Same(styles["WaitBrush"], path.Fill);
            Assert.Null(path.Stroke);
            // On and working: ember.
            flame.IsLive = true;
            Assert.Same(styles["EmberBrush"], path.Fill);
            Assert.Null(path.Stroke);
            // Live but switched off stays the outline: the switch speaks first.
            flame.IsOn = false;
            Assert.Same(styles["TextFillColorTertiaryBrush"], path.Stroke);
        });
    }
}
