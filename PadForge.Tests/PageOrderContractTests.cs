using System;
using System.IO;
using System.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Dashboard and Settings pages carry their sections in a decided
    /// order (2026-09-02). These pin the full sequence of title keys on each
    /// page, so a moved or added card fails here before it ships, and they
    /// pin the Dashboard's driver status strip as gone: Settings carries the
    /// HidHide, HIDMaestro, MIDI Services and SteamVR cards in full, so the
    /// Dashboard's three-row copy under the ED5D glyph was a duplicate.
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
                    "Dashboard_WebController",
                    "Dashboard_RemoteLink",
                    "Dashboard_HeadTracking",
                    "Dashboard_MotionServer",
                    "Dashboard_LightbarMirrors",
                    "Dashboard_Sensa",
                    "Dashboard_Overlays",
                    "Dashboard_TouchpadOverlay",
                });

            // The services sit under the Services header, and the header
            // sits after the slot cards.
            int services = page.IndexOf("x:Name=\"ServicesHeader\"", StringComparison.Ordinal);
            int slots = page.IndexOf("{Binding Dashboard_VirtualControllers,", StringComparison.Ordinal);
            int web = page.IndexOf("{Binding Dashboard_WebController,", StringComparison.Ordinal);
            Assert.True(slots < services && services < web, "the Services header divides the slot cards from the services");
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
            Assert.Equal(10, resx.Length);
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
