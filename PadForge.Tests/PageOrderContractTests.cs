using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Home, Extras and Settings carry their sections in a decided order
    /// (2026-09-02, and 2026-11-10 for Home and Extras). These pin the full
    /// sequence on each page, so a moved or added card fails here before it
    /// ships, and they pin the driver status strip the Dashboard once had as
    /// gone: Settings carries the HidHide, HIDMaestro, MIDI Services and
    /// SteamVR rows in full in its Drivers card, so the three-row copy under
    /// the ED5D glyph was a duplicate.
    /// </summary>
    public class PageOrderContractTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string RepoText(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        /// <summary>Each title binding must appear exactly once and after the
        /// one before it. The needle is the title's own binding text, so a
        /// description or tooltip that reuses the key's prefix never matches.</summary>
        private static void AssertTitleOrder(string page, string prefix, string suffix, string[] keys)
        {
            int last = -1;
            foreach (var key in keys)
            {
                string needle = prefix + key + suffix;
                int at = page.IndexOf(needle, StringComparison.Ordinal);
                Assert.True(at >= 0, key + " has no title binding on the page");
                Assert.True(at == page.LastIndexOf(needle, StringComparison.Ordinal), key + " has more than one title binding");
                Assert.True(at > last, key + " is out of order");
                last = at;
            }
        }

        /// <summary>Home (#574), top to bottom: the page's title, the
        /// engine's card, the offer, the controllers under their heading,
        /// and the extras in use under theirs with the line and the button
        /// at the foot. The engine's card has no heading over it. Each
        /// extra is listed once, in the catalog's order and by the
        /// catalog's own title.</summary>
        [Fact]
        public void Home_SectionsRunInTheDecidedOrder()
        {
            string page = RepoText("PadForge.App", "Views", "HomePage.xaml");
            const string heading = ", Source={x:Static strings:Strings.Instance}}";
            int last = -1;
            foreach (string needle in new[]
            {
                "<TextBlock x:Name=\"HomeTitle\"",
                "<Border x:Name=\"EngineCard\"",
                "<Border x:Name=\"HomeOffer\"",
                "Text=\"{Binding Home_Controllers" + heading,
                "<ItemsControl x:Name=\"SlotsItemsControl\"",
                "Value=\"{Binding Home_ExtrasInUse" + heading,
                "<controls:TileGridPanel x:Name=\"ExtrasInUseRows\"",
                "<StackPanel x:Name=\"ExtrasFoot\"",
            })
            {
                int at = page.IndexOf(needle, StringComparison.Ordinal);
                Assert.True(at >= 0, needle + " is not on Home");
                Assert.True(at == page.LastIndexOf(needle, StringComparison.Ordinal), needle + " is on Home twice");
                Assert.True(at > last, needle + " is out of order");
                last = at;
            }
            // A heading is drawn as written (the SectionTitle style), and
            // the engine's card has none.
            Assert.DoesNotContain("UpperConverter", page);
            Assert.DoesNotContain("Text=\"{Binding Dashboard_InputEngine" + heading, page);
            AssertTitleOrder(page,
                "Title=\"{Binding ", ", Source={x:Static strings:Strings.Instance}}\"",
                ExtrasTitleKeys);

            // The ten services left for the Extras page (#574), with their
            // group header and the strings that named the group. The
            // Dashboard's own title and its controllers heading went with
            // the page, and the status bar's tour stop with the status bar.
            Assert.DoesNotContain("ServicesSection", page);
            Assert.DoesNotContain("ServicesHeader", page);
            string designer = RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            foreach (string gone in new[]
            {
                "Dashboard_Services", "Tour_Services_Title", "Tour_Services_Body",
                "Dashboard_Title", "Dashboard_VirtualControllers", "Tour_Status_Title", "Tour_Status_Body",
            })
            {
                Assert.DoesNotContain(gone, designer);
                foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings"), "Strings*.resx"))
                    Assert.DoesNotContain("name=\"" + gone + "\"", File.ReadAllText(f));
            }
        }

        /// <summary>The extras' title keys, in catalog order: More Ways to
        /// Play, Send It Elsewhere, On Screen.</summary>
        private static readonly string[] ExtrasTitleKeys =
        {
            "Dashboard_WebController",
            "Dashboard_HeadTracking",
            "Dashboard_GameTelemetry",
            "Dashboard_TwitchChat",
            "Dashboard_RemoteLink",
            "Dashboard_MotionServer",
            "Dashboard_GameMods",
            "Dashboard_Overlays",
            "Dashboard_TouchpadOverlay",
            "Dashboard_ScreenOverlay",
        };

        /// <summary>The Extras page carries the ten extras in the decided
        /// order three times over: the catalog's tiles, the pages' titles and
        /// the key list the page's code shows them by. The three groups head
        /// the tiles in order, each over its own.</summary>
        [Fact]
        public void Extras_TilesAndPagesRunInTheDecidedOrder()
        {
            string page = RepoText("PadForge.App", "Views", "ExtrasPage.xaml");

            // One tile for each extra, in order.
            AssertTitleOrder(page,
                "Title=\"{Binding ", ", Source={x:Static strings:Strings.Instance}}\"",
                ExtrasTitleKeys);

            // One page for each, in the same order, under its own heading.
            AssertTitleOrder(page,
                "AutomationProperties.HeadingLevel=\"Level1\" Text=\"{Binding ", ", Source={x:Static strings:Strings.Instance}}\"",
                new[] { "Extras_Title" }.Concat(ExtrasTitleKeys).ToArray());

            // The groups, each ahead of its first tile and after the last
            // tile of the group before it.
            int At(string needle)
            {
                int at = page.IndexOf(needle, StringComparison.Ordinal);
                Assert.True(at >= 0, needle + " is not on the page");
                Assert.True(at == page.LastIndexOf(needle, StringComparison.Ordinal), needle + " is on the page twice");
                return at;
            }
            int play = At("{Binding Extras_Group_Play,"), send = At("{Binding Extras_Group_Send,"), screen = At("{Binding Extras_Group_Screen,");
            int web = At("x:Name=\"WebControllerTile\""), twitch = At("x:Name=\"TwitchChatTile\"");
            int remote = At("x:Name=\"RemoteLinkTile\""), mods = At("x:Name=\"GameModsTile\"");
            int overlays = At("x:Name=\"OverlaysTile\""), last = At("x:Name=\"ScreenOverlayTile\"");
            Assert.True(play < web && twitch < send && send < remote && mods < screen && screen < overlays && overlays < last,
                "a group heading is not over its own tiles");

            // The code's list is the same ten keys in the same order, and
            // each names a tile, a page and a way back.
            string[] keys = PadForge.Views.ExtrasPage.Keys.ToArray();
            Assert.Equal(ExtrasTitleKeys.Select(k => k.Substring("Dashboard_".Length)), keys);
            int before = -1;
            foreach (string key in keys)
            {
                int tile = At("x:Name=\"" + key + "Tile\" Key=\"" + key + "\"");
                int panel = At("<StackPanel x:Name=\"" + key + "Page\" Visibility=\"Collapsed\">");
                At("AutomationProperties.AutomationId=\"Extras_Back_" + key + "\"");
                Assert.True(tile < panel && panel > before, key + " is out of order");
                before = panel;
            }
        }

        /// <summary>The strings the Extras pages stopped using (#574) left
        /// the string tables: Head Tracking's More hint is the three
        /// settings' own names now, and the Web Controller and Remote Link
        /// pages dropped their old captions. The More row keeps
        /// Extras_More_HeadTracking as its AutomationId, which is no
        /// string.</summary>
        [Fact]
        public void Extras_DroppedStringsLeftTheStringTables()
        {
            string designer = RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            foreach (string gone in new[]
            {
                "Extras_More_HeadTracking", "Dashboard_WebFooter", "Dashboard_WebScanToOpen", "Dashboard_WebPlainLocalAddress",
                "RemoteLink_NearbyTitle", "RemoteLink_PeerNamePlaceholder", "RemoteLink_Online",
            })
            {
                Assert.DoesNotContain("\"" + gone + "\"", designer);
                foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings"), "Strings*.resx"))
                    Assert.DoesNotContain("name=\"" + gone + "\"", File.ReadAllText(f));
            }
            Assert.Contains("AutomationProperties.AutomationId=\"Extras_More_HeadTracking\"", RepoText("PadForge.App", "Views", "ExtrasPage.xaml"));
        }

        [Fact]
        public void Home_CarriesNoDriverStatusStrip()
        {
            string page = RepoText("PadForge.App", "Views", "HomePage.xaml");
            Assert.DoesNotContain("&#xED5D;", page);
            Assert.DoesNotContain("Dashboard_Drivers", page);
            Assert.DoesNotContain("HidHideStatusText", page);
            Assert.DoesNotContain("MidiServicesStatusText", page);
            Assert.DoesNotContain("SteamVrStatusText", page);

            // The key left with the strip, from every locale and the Designer.
            string designer = RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            Assert.DoesNotContain("Dashboard_Drivers", designer);
            var resx = Directory.GetFiles(Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings"), "Strings*.resx");
            Assert.Equal(PadForge.Common.LanguageCatalog.All.Count, resx.Length);
            foreach (var f in resx)
                Assert.DoesNotContain("name=\"Dashboard_Drivers\"", File.ReadAllText(f));

            // The Dashboard VM no longer carries the strip's display text.
            string vm = RepoText("PadForge.App", "ViewModels", "DashboardViewModel.cs");
            Assert.DoesNotContain("HidHideStatusText", vm);
            Assert.DoesNotContain("MidiServicesStatusText", vm);
            Assert.DoesNotContain("SteamVrStatusText", vm);

            // Settings still carries all three rows.
            string settings = RepoText("PadForge.App", "Views", "SettingsPage.xaml");
            Assert.Contains("{Binding HidHideStatusText}", settings);
            Assert.Contains("{Binding MidiServicesStatusText}", settings);
            Assert.Contains("{Binding SteamVrStatusText}", settings);
        }

        /// <summary>Settings (#574): the five group buttons, the five groups'
        /// panels in the same order, and each card's title inside its group's
        /// panel in the board's order. The rows that were cards once (the
        /// handheld buttons, the keyboard screens, the Falcon, each driver)
        /// carry no card title now.</summary>
        [Fact]
        public void Settings_CardsRunInTheDecidedOrder()
        {
            string page = RepoText("PadForge.App", "Views", "SettingsPage.xaml");
            string[] groups = { "General", "Input", "Hardware", "Drivers", "Data" };
            AssertTitleOrder(page, "Content=\"{Binding Settings_Group_", ", Source={x:Static strings:Strings.Instance}}\"", groups);
            AssertTitleOrder(page, "x:Name=\"", "Group\" ", groups);

            const string suffix = ", Source={x:Static strings:Strings.Instance}}\" Style=\"{StaticResource CardTitle}\"";
            var titles = new (string Key, string Group)[]
            {
                ("Settings_Language", "General"),
                ("Settings_Appearance", "General"),
                ("Settings_Window", "General"),
                ("Settings_Updates", "General"),
                ("Settings_InputEngine", "Input"),
                ("Settings_AssignOffer", "Input"),
                ("Settings_BatteryNotify", "Input"),
                ("Settings_HardwareSupport", "Hardware"),
                ("Settings_Group_Drivers", "Drivers"),
                ("Settings_CommunityConfigs", "Data"),
                ("Settings_SettingsFile", "Data"),
                ("Settings_Diagnostics", "Data"),
            };
            AssertTitleOrder(page, "{Binding ", suffix, titles.Select(t => t.Key).ToArray());
            for (int g = 0; g < groups.Length; g++)
            {
                int start = page.IndexOf("x:Name=\"" + groups[g] + "Group\" ", StringComparison.Ordinal);
                int end = g + 1 < groups.Length
                    ? page.IndexOf("x:Name=\"" + groups[g + 1] + "Group\" ", StringComparison.Ordinal)
                    : page.Length;
                foreach (var (key, group) in titles.Where(t => t.Group == groups[g]))
                {
                    int at = page.IndexOf("{Binding " + key + suffix, StringComparison.Ordinal);
                    Assert.True(at > start && at < end, key + " is not inside the " + group + " group's panel");
                }
            }
            foreach (string row in new[]
                     {
                         "Settings_Handheld", "Settings_KeyboardScreens_Title", "Settings_Falcon_Title", "Settings_HidHide",
                         "Settings_HIDMaestro", "Settings_BthPS3", "Settings_MidiServices", "Settings_SteamVR",
                     })
                Assert.DoesNotContain("{Binding " + row + suffix, page, StringComparison.Ordinal);
        }
    }
}
