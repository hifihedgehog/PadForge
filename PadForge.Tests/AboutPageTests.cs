using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The About page as its board draws it (#574): PadForge with its version,
    /// its update state and four buttons, then the invitation, the overview,
    /// the credits and the license. The credits show their first five rows
    /// until Show All Credits opens the list, and the field over them finds a
    /// credit by its name, its description or its license. PublishedFactsTests
    /// holds each credit's row and chip.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AboutPageTests
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        private const string S = ", Source={x:Static strings:Strings.Instance}}";

        private static string PageText() => ExtrasPageTests.RepoText("PadForge.App", "Views", "AboutPage.xaml");

        private static XDocument Page() => XDocument.Parse(PageText());

        private static string Attr(XElement e, string local) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == local)?.Value;

        /// <summary>The license keys whose text is a license's own identifier
        /// or name, which reads the same in every language.</summary>
        private static readonly string[] IdentifierChips =
        {
            "About_License_Mit", "About_License_Apache2", "About_License_Bsd3", "About_License_Bsd2", "About_License_Bsl1",
            "About_License_Isc", "About_License_Mpl2", "About_License_Unlicense", "About_License_Zlib", "About_License_Gpl2",
            "About_License_Gpl2OrLater", "About_License_Gpl3", "About_License_Gpl3OrLater", "About_License_Agpl3",
            "About_License_Lgpl21OrLater", "About_License_Lgpl3", "About_License_Lgpl3OrLater", "About_License_CcByNcSa4",
            "About_License_CcBy3", "About_License_CgTrader", "About_License_WyvrnEula",
        };

        /// <summary>Title, PadForge, An Invitation, Overview, Built With,
        /// License. No heading has a glyph before it or reads in
        /// capitals.</summary>
        [Fact]
        public void ThePageReadsTopToBottomAsItsBoard()
        {
            var column = Page().Root!.Elements().Single(e => e.Name.LocalName == "ScrollViewer").Elements().Single();
            // Every page's gutters, and the column Settings reads in: 1040
            // at most at the page's start, capped by the panel itself.
            Assert.Equal("StackPanel", column.Name.LocalName);
            Assert.Equal("36,26,36,36", Attr(column, "Margin"));
            Assert.Equal("1040", Attr(column, "MaxWidth"));
            Assert.Equal("Left", Attr(column, "HorizontalAlignment"));
            var parts = column.Elements().ToList();

            // The title every page has: 28 in the display face.
            var title = parts[0];
            Assert.Equal("AboutTitle", (string)title.Attribute(Xaml + "Name"));
            Assert.Equal("{Binding About_Title" + S, Attr(title, "BidiText.Text"));
            Assert.Equal("28", Attr(title, "FontSize"));
            Assert.Equal("{StaticResource DisplayFontFamily}", Attr(title, "FontFamily"));
            Assert.Equal("40.6", Attr(title, "LineHeight"));
            Assert.Equal("BlockLineHeight", Attr(title, "LineStackingStrategy"));
            Assert.Equal("Level1", Attr(title, "AutomationProperties.HeadingLevel"));

            var cards = parts.Skip(1).ToList();
            Assert.Equal(5, cards.Count);
            Assert.All(cards, card =>
            {
                Assert.Equal("Border", card.Name.LocalName);
                Assert.Equal("{StaticResource CardBorder}", Attr(card, "Style"));
            });
            Assert.Equal("PadForgeCard", (string)cards[0].Attribute(Xaml + "Name"));
            // Each of the other four is headed by its own first line.
            string Heading(XElement card) => Attr(card.Descendants().First(e => e.Name.LocalName == "TextBlock"), "BidiText.Text");
            Assert.Equal(new[]
            {
                "{Binding About_Testimony" + S, "{Binding About_Overview" + S, "{Binding About_BuiltWith" + S, "{Binding About_License" + S,
            }, cards.Skip(1).Select(Heading));
            Assert.All(cards.Skip(1), card => Assert.Equal("{StaticResource SectionTitle}",
                Attr(card.Descendants().First(e => e.Name.LocalName == "TextBlock"), "Style")));
            // The tagline credits x360ce and stays, under the overview.
            Assert.Contains(cards[2].Descendants(), e => Attr(e, "BidiText.Text") == "{Binding About_Tagline" + S);
            Assert.Contains(cards[4].Descendants(), e => Attr(e, "BidiText.Text") == "{Binding About_LicenseText" + S);

            string text = PageText();
            Assert.DoesNotContain("GlyphText", text);
            Assert.DoesNotContain("UpperConverter", text);
        }

        /// <summary>The name with the version beside it in the code face and
        /// the cold color, the line under it that says the update state, and
        /// Documentation, GitHub, Release Notes and Check for Updates in that
        /// order.</summary>
        [Fact]
        public void ThePadForgeCardShowsTheVersionTheUpdateStateAndFourButtons()
        {
            var card = Page().Descendants().Single(e => (string)e.Attribute(Xaml + "Name") == "PadForgeCard");

            var name = card.Descendants().Single(e => Attr(e, "AutomationProperties.AutomationId") == "About_Version");
            Assert.Equal("{Binding ApplicationVersion}", Attr(name, "ToolTip"));
            var runs = name.Elements().Where(e => e.Name.LocalName == "Run").ToList();
            Assert.Equal("{Binding Common_PadForge, Source={x:Static strings:Strings.Instance}, Mode=OneWay}", Attr(runs[0], "Text"));
            var version = runs[^1];
            Assert.Equal("{Binding VersionNumber, Mode=OneWay}", Attr(version, "Text"));
            Assert.Equal("{StaticResource TelemetryFontFamily}", Attr(version, "FontFamily"));
            Assert.Equal("{DynamicResource ColdBrush}", Attr(version, "Foreground"));
            Assert.Equal("14", Attr(version, "FontSize"));

            // The line is read out when it changes, and stands in ember while
            // an update waits: the ember words take, which the light theme
            // deepens.
            var line = card.Descendants().Single(e => (string)e.Attribute(Xaml + "Name") == "AboutSubLine");
            Assert.Equal("{Binding AboutSubLine}", Attr(line, "BidiText.Text"));
            Assert.Equal("True", Attr(line, "LiveText.Polite"));
            var waiting = line.Descendants().Single(e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding IsUpdateAvailable}", Attr(waiting, "Binding"));
            Assert.Equal("{DynamicResource EmberTextBrush}", Attr(waiting.Elements().Single(), "Value"));

            var buttons = card.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
            Assert.Equal(new[] { "About_Documentation", "About_GitHub", "About_ReleaseNotes", "About_CheckForUpdates" },
                buttons.Select(b => Attr(b, "AutomationProperties.AutomationId")));
            Assert.Equal(new[]
            {
                "{Binding About_Documentation" + S, "{Binding About_GitHub" + S, "{Binding Settings_ReleaseNotes" + S, "{Binding About_CheckForUpdates" + S,
            }, buttons.Select(b => Attr(b, "Content")));
            Assert.All(buttons, b =>
            {
                Assert.Equal("36", Attr(b, "MinHeight"));
                Assert.Equal("14,0", Attr(b, "Padding"));
            });
            Assert.Equal("Documentation_Click", Attr(buttons[0], "Click"));
            Assert.Equal("GitHub_Click", Attr(buttons[1], "Click"));
            Assert.Equal("{Binding OpenReleaseNotesCommand}", Attr(buttons[2], "Command"));
            Assert.Equal("{Binding CheckForUpdatesNowCommand}", Attr(buttons[3], "Command"));
            Assert.Equal("https://padforge.org/docs/", AboutPage.DocumentationUrl);
        }

        /// <summary>Every chip names a license key the string table holds,
        /// and the page uses each of the twenty-nine.</summary>
        [Fact]
        public void EveryChipNamesALicenseTheStringTableHolds()
        {
            string resx = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx");
            var inTable = Regex.Matches(resx, "<data name=\"(About_License_[A-Za-z0-9]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            Assert.Equal(29, inTable.Count);
            Assert.All(IdentifierChips, key => Assert.Contains(key, inTable));

            var chips = Page().Descendants()
                .Where(e => e.Name.LocalName == "Border" && Attr(e, "Style") == "{StaticResource CreditChip}")
                .Select(e => Regex.Match(Attr(e.Elements().Single(), "BidiText.Text") ?? string.Empty, @"^\{Binding (\w+),").Groups[1].Value)
                .ToList();
            Assert.Equal(147, chips.Count);
            Assert.All(chips, key => Assert.Contains(key, inTable));
            Assert.Equal(inTable.OrderBy(k => k, StringComparer.Ordinal), chips.Distinct().OrderBy(k => k, StringComparer.Ordinal));
        }

        /// <summary>A license's identifier or name is a legal identifier, so
        /// its chip reads as the English does in every language.</summary>
        [Fact]
        public void ALicensesIdentifierReadsTheSameInEveryLanguage()
        {
            static Dictionary<string, string> Table(string resx) => Regex
                .Matches(resx, "<data name=\"(About_License_[A-Za-z0-9]+)\" xml:space=\"preserve\">\\s*<value>(.*?)</value>", RegexOptions.Singleline)
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

            var english = Table(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"));
            var wrong = new List<string>();
            foreach (string suffix in LocaleFiles.CompleteSuffixes.Where(s => s.Length > 0))
            {
                var table = Table(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings" + suffix + ".resx"));
                foreach (string key in IdentifierChips)
                    if (!table.TryGetValue(key, out string text) || text != english[key])
                        wrong.Add("Strings" + suffix + ".resx " + key + ": " + (text ?? "(missing)"));
            }
            Assert.Empty(wrong);
        }

        /// <summary>The page reads in one column: every page's gutters and
        /// 1040 at most at the page's start, however wide the window. The
        /// column keeps its width whatever the credits search leaves in the
        /// list, and on the window's least width it is the page's width
        /// inside its gutters.</summary>
        [Fact]
        public void ThePageReadsInOneColumn_WhateverTheCreditsShow()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var page = new AboutPage { DataContext = new SettingsViewModel() };
                var column = (FrameworkElement)page.FindName("PageColumn");
                var card = (FrameworkElement)page.FindName("PadForgeCard");
                var field = (PadForge.Controls.SearchField)page.FindName("CreditsSearch");
                void Lay(double width)
                {
                    page.Measure(new Size(width, 4000));
                    page.Arrange(new Rect(0, 0, width, 4000));
                    page.UpdateLayout();
                }
                Rect On(FrameworkElement part) => part.TransformToAncestor(page).TransformBounds(new Rect(part.RenderSize));

                // A wide window: 1040 at the page's start, and no wider.
                Lay(1800);
                Assert.Equal(36, On(column).X, 3);
                Assert.Equal(1040, column.ActualWidth, 3);
                Assert.Equal(1040, card.ActualWidth, 3);

                // The credits search leaves one line. The column stays.
                field.Text = "qqzzqq";
                Lay(1800);
                Assert.Equal(1040, column.ActualWidth, 3);
                field.Text = string.Empty;

                // The window's least width.
                Lay(636);
                Assert.Equal(36, On(column).X, 3);
                Assert.Equal(636 - 72, column.ActualWidth, 3);
            }, TimeSpan.FromMinutes(2), "the About layout test timed out");
        }

        /// <summary>The credits on a made page: five rows over the button
        /// that opens the list, every row once it is open, every row that
        /// holds a query with no button under them, and the line that says no
        /// credit matches.</summary>
        [Fact]
        public void TheCreditsFoldAndTheFieldFindsACredit()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var page = new AboutPage { DataContext = new SettingsViewModel() };
                var list = (StackPanel)page.FindName("CreditsList");
                var field = (PadForge.Controls.SearchField)page.FindName("CreditsSearch");
                var buttonRow = (Border)page.FindName("ShowAllCreditsRow");
                var button = (ButtonBase)page.FindName("ShowAllCreditsButton");
                var none = (Border)page.FindName("NoCreditMatch");
                var noneText = (TextBlock)page.FindName("NoCreditMatchText");
                var rows = list.Children.Cast<UIElement>().ToList();
                List<UIElement> Shown() => rows.Where(r => r.Visibility == Visibility.Visible).ToList();
                void Click() => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                // Every text of a row: its name, its description and its chip.
                static IEnumerable<string> Texts(DependencyObject part)
                {
                    foreach (object child in LogicalTreeHelper.GetChildren(part))
                    {
                        if (child is TextBlock text) yield return BidiText.GetText(text);
                        else if (child is DependencyObject inner)
                            foreach (string below in Texts(inner)) yield return below;
                    }
                }
                bool Holds(UIElement row, string query) => Texts(row).Any(t => TextMatch.Contains(t, query));

                // Folded: the first five, over the button.
                Assert.Equal(148, rows.Count);
                Assert.Equal(rows.Take(AboutPage.FirstCredits), Shown());
                Assert.Equal(Visibility.Visible, buttonRow.Visibility);
                Assert.Equal(Strings.Instance.About_ShowAllCredits, button.Content);
                Assert.Equal(Visibility.Collapsed, none.Visibility);

                // Opened, and folded again.
                Click();
                Assert.Equal(rows, Shown());
                Assert.Equal(Strings.Instance.About_ShowFewerCredits, button.Content);
                Click();
                Assert.Equal(rows.Take(AboutPage.FirstCredits), Shown());
                Assert.Equal(Strings.Instance.About_ShowAllCredits, button.Content);

                // A query shows every row that holds it, wherever the fold
                // stands, and the button's row goes.
                field.Text = "zlib";
                var found = Shown();
                Assert.True(found.Count >= 2, "zlib found " + found.Count + " credits");
                Assert.All(rows, r => Assert.Equal(Holds(r, "zlib"), r.Visibility == Visibility.Visible));
                Assert.Contains(found, r => !rows.Take(AboutPage.FirstCredits).Contains(r));
                Assert.Equal(Visibility.Collapsed, buttonRow.Visibility);
                Assert.Equal(Visibility.Collapsed, none.Visibility);

                // Width and case are set aside, as the interface's language
                // compares text: full-width letters find MIT.
                field.Text = "ｍｉｔ";
                Assert.True(Shown().Count >= 39, "full-width mit found " + Shown().Count + " credits");

                // No credit holds the query: one line says so.
                field.Text = "qqzzqq";
                Assert.Empty(Shown());
                Assert.Equal(Visibility.Visible, none.Visibility);
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Instance.About_NoCreditMatch_Format, "qqzzqq"), BidiText.GetText(noneText));
                Assert.Equal(Visibility.Collapsed, buttonRow.Visibility);

                // An emptied field puts the fold back.
                field.Text = string.Empty;
                Assert.Equal(rows.Take(AboutPage.FirstCredits), Shown());
                Assert.Equal(Visibility.Visible, buttonRow.Visibility);
                Assert.Equal(Visibility.Collapsed, none.Visibility);
            }, TimeSpan.FromMinutes(3), "the About page run timed out");
        }
    }

    /// <summary>The line under PadForge's name on the About page (#574), and
    /// the page its Release Notes button opens before any check.</summary>
    public class SettingsAboutLineTests
    {
        [Fact]
        public void TheLineSaysTheUpdateStateAfterTheSubtitle()
        {
            var s = Strings.Instance;
            var vm = new SettingsViewModel();
            string With(string state) => string.Format(CultureInfo.CurrentCulture, s.About_SubtitleState_Format, s.About_Subtitle, state);

            // Before any check: the subtitle alone.
            Assert.Equal(s.About_Subtitle, vm.AboutSubLine);
            Assert.False(vm.IsUpToDate);

            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            // A check found this build the newest.
            vm.SetUpdateStatus(() => "PadForge 6.0.0 is up to date.", upToDate: true);
            Assert.True(vm.IsUpToDate);
            Assert.Equal(With(s.About_UpToDate), vm.AboutSubLine);
            Assert.Contains(nameof(SettingsViewModel.AboutSubLine), raised);

            // While a check runs, has failed or has found an update: the
            // Updates card's own sentence.
            raised.Clear();
            vm.SetUpdateStatus(() => "Checking for updates...");
            Assert.False(vm.IsUpToDate);
            Assert.Equal(With("Checking for updates..."), vm.AboutSubLine);
            Assert.Contains(nameof(SettingsViewModel.AboutSubLine), raised);

            // Cleared: the subtitle alone again.
            vm.SetUpdateStatus(null);
            Assert.Equal(s.About_Subtitle, vm.AboutSubLine);
        }

        /// <summary>The English, as the board writes it.</summary>
        [Fact]
        public void TheEnglishReadsAsTheBoardDoes()
        {
            static string English(string key) => Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture);
            Assert.Equal("Controller Mapping Utility. Up to date.",
                string.Format(CultureInfo.InvariantCulture, English("About_SubtitleState_Format"), English("About_Subtitle"), English("About_UpToDate")));
        }

        /// <summary>The About page shows Release Notes before any check, so
        /// with no update offered it opens the releases page. An address
        /// outside PadForge's repository opens nothing.</summary>
        [Fact]
        public void ReleaseNotesOpensTheReleasesPageWhileNoUpdateIsOffered()
        {
            string repo = "https://github.com/" + UpdateService.Repo;
            Assert.Equal(repo + "/releases", UpdateController.ReleaseNotesUrl(null));

            static UpdateOffer Offer(string page)
                => new UpdateOffer("6.0.1", false, 0, null, new Version(6, 0, 1), page, "PadForge.zip", "u", 1, new string('a', 64));
            Assert.Equal(repo + "/releases/tag/v6.0.1", UpdateController.ReleaseNotesUrl(Offer(repo + "/releases/tag/v6.0.1")));
            Assert.Null(UpdateController.ReleaseNotesUrl(Offer("https://example.com/releases/tag/v6.0.1")));
            Assert.Null(UpdateController.ReleaseNotesUrl(Offer(string.Empty)));
        }
    }
}
