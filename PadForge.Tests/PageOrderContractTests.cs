using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Dashboard, Extras and Settings pages carry their sections in a
    /// decided order (2026-09-02, and 2026-11-10 for Extras). These pin the
    /// full sequence of title keys on each page, so a moved or added card
    /// fails here before it ships, and they pin the Dashboard's driver status
    /// strip as gone: Settings carries the HidHide, HIDMaestro, MIDI Services
    /// and SteamVR cards in full, so the Dashboard's three-row copy under the
    /// ED5D glyph was a duplicate.
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

        [Fact]
        public void Dashboard_SectionsRunInTheDecidedOrder()
        {
            string page = RepoText("PadForge.App", "Views", "DashboardPage.xaml");
            AssertTitleOrder(page,
                "{Binding ", ", Source={x:Static strings:Strings.Instance}, Converter={StaticResource UpperConverter}}",
                new[]
                {
                    "Dashboard_InputEngine",
                    "Dashboard_VirtualControllers",
                });

            // The ten services left for the Extras page (#574), with their
            // group header and the strings that named the group.
            Assert.DoesNotContain("ServicesSection", page);
            Assert.DoesNotContain("ServicesHeader", page);
            foreach (string key in ExtrasTitleKeys)
                Assert.DoesNotContain("{Binding " + key + ",", page);
            string designer = RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            foreach (string gone in new[] { "Dashboard_Services", "Tour_Services_Title", "Tour_Services_Body" })
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

        [Fact]
        public void Dashboard_DriverStatusStripIsGone()
        {
            string page = RepoText("PadForge.App", "Views", "DashboardPage.xaml");
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

        [Fact]
        public void Settings_CardsRunInTheDecidedOrder()
        {
            string page = RepoText("PadForge.App", "Views", "SettingsPage.xaml");
            AssertTitleOrder(page,
                "{Binding ", ", Source={x:Static strings:Strings.Instance}}\" Style=\"{StaticResource CardTitle}\"",
                new[]
                {
                    "Settings_Language",
                    "Settings_Appearance",
                    "Settings_Window",
                    "Settings_Updates",
                    "Settings_InputEngine",
                    "Settings_AssignOffer",
                    "Settings_Handheld",
                    "Settings_BatteryNotify",
                    "Settings_HidHide",
                    "Settings_HIDMaestro",
                    "Settings_MidiServices",
                    "Settings_SteamVR",
                    "Settings_CommunityConfigs",
                    "Settings_SettingsFile",
                    "Settings_Diagnostics",
                });
        }
    }
}
