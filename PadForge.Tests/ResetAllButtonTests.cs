using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A card's Reset All (#574, #600): a tooltip that names its card, which
    /// is also the name a screen reader reads after its words (App.xaml,
    /// ResetAllButton, ResetAllNameConverter), and the class of the strictest row it
    /// resets. The catalog leaves the look out (SearchIndexBuilder.ActionKey)
    /// but for a plugin's two, which programs press, and the class holds
    /// should another ever be listed, as the Window card's does.
    /// </summary>
    public class ResetAllButtonTests
    {
        private const string WpfUi = "http://schemas.lepo.co/wpfui/2022/xaml";

        [Fact]
        public void EveryResetAll_HasATooltipThatNamesItsCard_AndReadsAsIt()
        {
            var wrong = new List<string>();
            int seen = 0;
            foreach (string file in ReachMarkup.Files())
            {
                var doc = XDocument.Parse(File.ReadAllText(file), LoadOptions.SetLineInfo);
                foreach (var e in doc.Descendants().Where(d => ReachMarkup.Attr(d, "Content") is { } c && Regex.IsMatch(c, @"\bPad_ResetAll\b")))
                {
                    seen++;
                    string where = Path.GetFileName(file) + ":" + ((IXmlLineInfo)e).LineNumber;
                    // The look builds the name from the tooltip.
                    bool look = e.Name.NamespaceName == WpfUi && ReachMarkup.Attr(e, "Style") == "{StaticResource ResetAllButton}";
                    if (!look)
                        wrong.Add(where + " reads as Reset All alone: no ResetAllButton look");
                    if (ReachMarkup.Attr(e, "ToolTip") == null && !e.Elements().Any(c => c.Name.LocalName.EndsWith(".ToolTip", System.StringComparison.Ordinal)))
                        wrong.Add(where + " has no tooltip that names its card");
                }
            }
            Assert.True(seen > 50, "only " + seen + " Reset All buttons read");
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        /// <summary>A plugin's declared settings are a program's to change
        /// (#600 items 17 and 19), and so is their Reset All. The Test
        /// Controller's card and a plugin device's card keep theirs in the
        /// catalog, free, under the Reset All look, and no other Reset All is
        /// listed.</summary>
        [Fact]
        public void APluginsResetAll_StaysAFreeCatalogAction_UnderTheResetAllLook()
        {
            string[] ids = { "Devices_PluginResetAll", "Plugins_TestController_ResetAll" };
            var index = PadForge.Search.SearchIndexBuilder.Build(PadForge.Search.SearchSources.Load());
            var listed = index.Where(e => e.Label?.Key == "Pad_ResetAll").ToList();
            Assert.Equal(ids, listed.Select(e => e.AutomationId).OrderBy(i => i, System.StringComparer.Ordinal));
            Assert.All(listed, e =>
            {
                Assert.Equal(PadForge.Search.SearchKind.Action, e.Kind);
                Assert.Equal(ReachClass.Free, AssistantCatalog.ReachOf(e));
            });
            foreach (string id in ids)
            {
                var button = ReachMarkup.Files()
                    .SelectMany(f => XDocument.Parse(File.ReadAllText(f), LoadOptions.SetLineInfo).Descendants())
                    .Single(d => ReachMarkup.Attr(d, "AutomationProperties.AutomationId") == id);
                Assert.Equal(WpfUi, button.Name.NamespaceName);
                Assert.Equal("{StaticResource ResetAllButton}", ReachMarkup.Attr(button, "Style"));
            }
        }

        /// <summary>The class and reason an element of a page ends up with,
        /// found by its AutomationId: the strictest class on the way down,
        /// and the reason of the nearest Asks on the way up, as Reach.Of
        /// reads them.</summary>
        private static (ReachClass Class, ReachReason Why) ClassOf(string page, string automationId)
        {
            var (element, cls, _, _) = ReachMarkup.Read(File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Views", page)))
                .Single(r => ReachMarkup.Attr(r.Element, "AutomationProperties.AutomationId") == automationId);
            for (var at = element; at != null; at = at.Parent)
                if (ReachMarkup.Attr(at, "Reach.Class") == "Asks")
                    return (cls, cls == ReachClass.Asks && System.Enum.TryParse(ReachMarkup.Attr(at, "Reach.Why"), out ReachReason why) ? why : ReachReason.None);
            return (cls, ReachReason.None);
        }

        /// <summary>The Updates card's Reset All turns Check for Updates
        /// Automatically back on, which installs a found update with
        /// Install Updates Automatically on (UpdateController.AutoInstallOn),
        /// as the Window card's Reset All turns Start at Login off.</summary>
        [Fact]
        public void TheUpdatesCardsResetAll_AsksAsRunningAProgram()
        {
            var (cls, why) = ClassOf("SettingsPage.xaml", "Settings_ResetAll_Updates");
            Assert.Equal((ReachClass.Asks, ReachReason.RunsProgram), (cls, why));
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Automation_Ask_RunsProgram, AssistantToolWords.Reason(why));
            Assert.Equal(ClassOf("SettingsPage.xaml", "Settings_ResetAll_Window"), (cls, why));
        }

        /// <summary>The Devices Settings card's Reset All turns Hide from
        /// Games off first (DevicesViewModel.SettingResets.cs), which shows
        /// the device to games again, as the row's own switch does.</summary>
        [Fact]
        public void TheDevicesSettingsCardsResetAll_AsksAsADriverChange()
        {
            var (cls, why) = ClassOf("DevicesPage.xaml", "Devices_ResetAll");
            Assert.Equal((ReachClass.Asks, ReachReason.Drivers), (cls, why));
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Automation_Ask_Drivers, AssistantToolWords.Reason(why));
        }
    }
}
