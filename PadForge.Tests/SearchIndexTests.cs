using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PadForge.Search;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// Search's index (#574), built from the pages' markup on disk as the app
    /// builds it from the copies it carries. Every reset button of the five
    /// pages is a setting Search finds, or sits where the rules leave it out
    /// on purpose, or has no label of its own. The index places each entry
    /// where it shows, and the board's query finds the board's rows.
    /// </summary>
    public class SearchIndexTests
    {
        private readonly ITestOutputHelper _output;

        public SearchIndexTests(ITestOutputHelper output) => _output = output;

        private static Dictionary<SearchPage, string> Markup()
            => SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));

        private static SearchIndex Index() => SearchIndexBuilder.Read(Markup(), PadPage.TabOfGroupName);

        private static readonly Regex ResetMarkup = new(
            @"<\w+:SettingResetButton[\s/>]|Style=""\{StaticResource (?:ResetButton|ResetButtonTight|QuietResetButton)\}""");

        [Fact]
        public void EveryResetButton_IsAnEntry_LeftOutOnPurpose_OrHasNoLabel()
        {
            var index = Index();
            foreach (var (page, text) in Markup())
            {
                int inMarkup = ResetMarkup.Matches(text).Count;
                var read = index.Resets.Where(r => r.Page == page).ToList();
                int entries = read.Count(r => r.Outcome == ResetOutcome.Entry);
                int left = read.Count(r => r.Outcome == ResetOutcome.LeftOut);
                int bare = read.Count(r => r.Outcome == ResetOutcome.NoLabel);
                _output.WriteLine($"{page}: {inMarkup} reset buttons, {entries} entries, {left} left out, {bare} with no label of their own");
                Assert.True(inMarkup > 0, page + " has no reset button in its markup");
                Assert.Equal(inMarkup, read.Count);
                Assert.True(entries > 0, page + " gives Search no setting");
            }
            var settings = index.Entries.Where(e => e.Kind == SearchKind.Setting).ToList();
            _output.WriteLine($"{settings.Count} settings, {index.Entries.Count(e => e.Kind == SearchKind.Card)} cards, "
                + $"{index.Entries.Count(e => e.Kind == SearchKind.Action)} actions, {index.Entries.Count(e => e.Kind == SearchKind.Extra)} extras, "
                + $"{index.Entries.Count(e => e.Kind == SearchKind.Section)} sections, {index.Entries.Count(e => e.Kind == SearchKind.Part)} parts");
            // The rows with no label are the color channels, which their
            // cards stand for.
            Assert.InRange(index.Resets.Count(r => r.Outcome == ResetOutcome.NoLabel), 0, 30);
        }

        [Fact]
        public void EveryLabel_IsAStringOrAPathOnTheData_AndEveryCardHasATitle()
        {
            string resx = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx");
            var keys = new HashSet<string>(Regex.Matches(resx, "<data name=\"([^\"]+)\"").Select(m => m.Groups[1].Value));
            foreach (var entry in Index().Entries)
            {
                foreach (var label in new[] { entry.Label, entry.Qualifier, entry.CardTitle })
                {
                    if (label == null) continue;
                    Assert.True(label.Key != null ^ label.Path != null, entry + " has a label that is neither a key nor a path");
                    if (label.Key != null) Assert.True(keys.Contains(label.Key), label.Key + " is no string");
                }
                Assert.NotNull(entry.Label);
            }
        }

        [Fact]
        public void EveryControllerPageEntry_SitsInASectionAndTheTabThatShowsIt()
        {
            foreach (var entry in Index().Entries.Where(e => e.Page == SearchPage.Pad && e.Section >= 0))
            {
                if (entry.Kind == SearchKind.Section) continue;
                // The macro editor's own menus (#600) stand in its section,
                // on no tab of their own.
                if (entry.Kind == SearchKind.MenuItem && entry.Section == PadViewModel.MacrosSection) continue;
                if (entry.Kind == SearchKind.Part)
                {
                    Assert.Equal(PadViewModel.SectionOfTab(entry.Tab), entry.Section);
                    continue;
                }
                Assert.InRange(entry.Section, 0, 4);
                Assert.True(PadViewModel.SectionOfTab(entry.Tab) == entry.Section,
                    $"{entry.Label.Key ?? entry.Label.Path} (order {entry.Order}) is in section {entry.Section} on tab {entry.Tab}");
            }
            // Positive control: the six sections and the four parts.
            var index = Index().Entries;
            Assert.Equal(6, index.Count(e => e.Kind == SearchKind.Section));
            Assert.Equal(4, index.Count(e => e.Kind == SearchKind.Part));
        }

        [Fact]
        public void TabOfGroupName_NamesEveryGroupOfThePage()
        {
            var page = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var groups = page.Descendants().Where(e => e.Name.LocalName == "GroupBox")
                .Select(e => (string)e.Attribute(x + "Name")).Where(n => n != null && n.StartsWith("Tab", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(groups);
            foreach (string name in groups.Append("TabBassShakers").Append("TabMouse"))
                Assert.True(PadPage.TabOfGroupName.ContainsKey(name), name + " is not in PadPage.TabOfGroupName");
            foreach (var (name, tab) in PadPage.TabOfGroupName)
                Assert.InRange(PadViewModel.SectionOfTab(tab), 1, 4);
        }

        /// <summary>The Search board's query: "deadzone" finds the stick
        /// card's deadzone per stick, a mapping row's deadzone in its
        /// options, and Calibrate Center on the stick's card.</summary>
        [Fact]
        public void TheBoardsQuery_FindsTheBoardsRows()
        {
            var index = Index().Entries;
            var stick = index.Single(e => e.Kind == SearchKind.Setting && e.Label.Key == "Pad_Sticks_Section_DeadZone" && e.ItemsPath == "StickConfigs");
            Assert.Equal(1, stick.Section);
            Assert.Equal(3, stick.Tab);
            Assert.Equal("Title", stick.Qualifier?.Path);
            Assert.Equal("DeadZoneBoth", stick.Value?.Path);
            Assert.Equal("%", stick.Value?.UnitText);
            Assert.Equal("StickDeadZoneBothSlider", stick.TargetAutomationId);

            var row = index.First(e => e.Kind == SearchKind.Setting && e.Label.Key == "Pad_HeaderDeadZone");
            Assert.True(row.RowOptions);
            Assert.Equal(0, row.Section);
            Assert.Equal(PadViewModel.MappingsTabIndex, row.Tab);

            var calibrate = index.Single(e => e.Kind == SearchKind.Action && e.Label.Key == "Pad_CalibrateCenter" && e.ItemsPath == "StickConfigs");
            Assert.Equal("Title", calibrate.Qualifier?.Path);

            // Nothing of Macros & Menus but its two parts and the menus of
            // its buttons (#600), which ask.
            Assert.DoesNotContain(index, e => e.Page == SearchPage.Pad && e.Section == PadViewModel.MacrosSection && e.Kind != SearchKind.Part && e.Kind != SearchKind.Section
                && !(e.Kind == SearchKind.MenuItem && e.Reach == PadForge.Common.ReachClass.Asks));
        }

        /// <summary>The copies the app carries are the files on disk.</summary>
        [Fact]
        public void TheAppCarriesThePagesMarkup()
        {
            var carried = SearchSources.Load();
            Assert.Equal(5, carried.Count);
            foreach (var (page, text) in Markup())
                Assert.Equal(text.Replace("\r\n", "\n"), carried[page].Replace("\r\n", "\n").TrimStart('\uFEFF'));
        }
        /// <summary>Every setting under a titled ExtraCard of the Extras
        /// pages carries that card's title in Search, as a setting under a
        /// CardBorder carries its card's: the page's ExtraCard is based on
        /// CardBorder and its ExtraCardTitle on CardTitle. Each reset the
        /// index made an entry of is found in the markup by its line, and
        /// its own card is the nearest ExtraCard around it.</summary>
        [Fact]
        public void EverySettingUnderATitledExtraCard_CarriesThatTitle()
        {
            string text = Markup()[SearchPage.Extras];
            var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
            const string Card = "{StaticResource ExtraCard}";
            const string Title = "{StaticResource ExtraCardTitle}";
            string StyleOf(XElement e) => (string)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Style" && a.Name.Namespace == XNamespace.None);
            bool IsCard(XElement e) => e.Name.LocalName == "Border" && StyleOf(e) == Card;
            // A card's title: its first ExtraCardTitle before any card inside it.
            string TitleOf(XElement card)
            {
                foreach (var e in card.Descendants())
                {
                    if (e.Ancestors().TakeWhile(a => a != card).Any(IsCard)) continue;
                    if (e.Name.LocalName == "TextBlock" && StyleOf(e) == Title)
                    {
                        var m = Regex.Match((string)e.Attribute("Text") ?? string.Empty, @"^\{Binding (\w+), Source=\{x:Static strings:Strings\.Instance\}\}$");
                        return m.Success ? m.Groups[1].Value : null;
                    }
                }
                return null;
            }
            var resetsByLine = doc.Descendants().Where(e => e.Name.LocalName == "SettingResetButton")
                .ToDictionary(e => ((System.Xml.IXmlLineInfo)e).LineNumber);

            var index = Index();
            var lines = index.Resets.Where(r => r.Page == SearchPage.Extras && r.Outcome == ResetOutcome.Entry).Select(r => r.Line).ToList();
            var settings = index.Entries.Where(e => e.Page == SearchPage.Extras && e.Kind == SearchKind.Setting).OrderBy(e => e.Order).ToList();
            // One entry for each reset the index read, in the same order.
            Assert.Equal(lines.Count, settings.Count);

            int underTitled = 0;
            var titles = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Count; i++)
            {
                if (!resetsByLine.TryGetValue(lines[i], out var reset)) continue;
                var card = reset.Ancestors().FirstOrDefault(IsCard);
                string title = card == null ? null : TitleOf(card);
                if (title == null) continue;
                underTitled++;
                titles.Add(title);
                var entry = settings[i];
                Assert.True(entry.CardTitle?.Key == title || entry.Qualifier?.Key == title,
                    $"the setting {entry.Label.Key ?? entry.Label.Path} on line {lines[i]} does not carry its card's title {title}");
            }
            // Positive control: the Extras pages hold settings under titled
            // ExtraCards, and each such card is an entry of its own.
            Assert.True(underTitled >= 20, underTitled + " settings under titled ExtraCards");
            foreach (string title in titles)
                Assert.Contains(index.Entries, e => e.Page == SearchPage.Extras && e.Kind == SearchKind.Card && e.Label.Key == title);
            _output.WriteLine($"{underTitled} settings under {titles.Count} titled ExtraCards");
        }
    }
}
