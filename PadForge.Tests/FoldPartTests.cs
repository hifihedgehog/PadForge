using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Controls;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The shared parts of the PadForge 6 layout (#574): a card that folds
    /// to one line, the More row that opens the rest of a card, the set that
    /// remembers which of them are open, and the quiet rest state of the
    /// reset buttons.
    /// </summary>
    [Collection("CardFoldStateStatics")]
    public class FoldPartTests
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

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
            Assert.True(thread.Join(30000), "the fold part test timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>Counts <see cref="CardFoldState.Changed"/> for the length
        /// of a test and leaves the set empty afterward.</summary>
        private sealed class ChangeCount : IDisposable
        {
            public int Changes;
            private readonly EventHandler _handler;

            public ChangeCount()
            {
                CardFoldState.Load(null);
                _handler = (_, _) => Changes++;
                CardFoldState.Changed += _handler;
            }

            public void Dispose()
            {
                CardFoldState.Changed -= _handler;
                CardFoldState.Load(null);
            }
        }

        // ── the set ────────────────────────────────────────────────────

        [Fact]
        public void Set_RecordsARealChange_AndReportsItOnce()
        {
            using var count = new ChangeCount();

            CardFoldState.Set("Pad.Sticks.More", true);
            Assert.True(CardFoldState.IsOpen("Pad.Sticks.More"));
            Assert.Equal(1, count.Changes);

            // Opening an open card is no change, so nothing is saved for it.
            CardFoldState.Set("Pad.Sticks.More", true);
            Assert.Equal(1, count.Changes);

            CardFoldState.Set("Pad.Sticks.More", false);
            Assert.False(CardFoldState.IsOpen("Pad.Sticks.More"));
            Assert.Equal(2, count.Changes);

            // A part with no key is not stored.
            CardFoldState.Set(null, true);
            CardFoldState.Set("", true);
            Assert.False(CardFoldState.IsOpen(null));
            Assert.False(CardFoldState.IsOpen(""));
            Assert.Empty(CardFoldState.Snapshot());
            Assert.Equal(2, count.Changes);
        }

        [Fact]
        public void Load_ReplacesTheSet_AndDoesNotMarkTheSettingsChanged()
        {
            using var count = new ChangeCount();
            CardFoldState.Set("a", true);
            Assert.Equal(1, count.Changes);

            int replaced = 0;
            EventHandler onReplaced = (_, _) => replaced++;
            CardFoldState.Replaced += onReplaced;
            try
            {
                CardFoldState.Load(new[] { "b", "", null, "c" });
            }
            finally
            {
                CardFoldState.Replaced -= onReplaced;
            }

            Assert.False(CardFoldState.IsOpen("a"));
            Assert.True(CardFoldState.IsOpen("b"));
            Assert.True(CardFoldState.IsOpen("c"));
            Assert.Equal(new[] { "b", "c" }, CardFoldState.Snapshot());
            Assert.Equal(1, replaced);
            // Reading the file is not an edit.
            Assert.Equal(1, count.Changes);
        }

        [Fact]
        public void Snapshot_ListsTheKeysInOrdinalOrder()
        {
            using var count = new ChangeCount();
            CardFoldState.Set("b", true);
            CardFoldState.Set("B", true);
            CardFoldState.Set("a", true);
            Assert.Equal(new[] { "B", "a", "b" }, CardFoldState.Snapshot());
        }

        // ── the settings file ──────────────────────────────────────────

        [Fact]
        public void TheSettingsFile_KeepsTheOpenCards_AndWritesNothingWhenNoneIsOpen()
        {
            var serializer = new XmlSerializer(typeof(AppSettingsData));

            var none = new AppSettingsData();
            Assert.Empty(none.OpenCards);
            var writer = new StringWriter();
            serializer.Serialize(writer, none);
            // A file with no card open carries no element for them.
            Assert.DoesNotContain("OpenCards", writer.ToString(), StringComparison.Ordinal);
            var noneBack = (AppSettingsData)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.Empty(noneBack.OpenCards);

            var some = new AppSettingsData { OpenCards = new List<string> { "Pad.Sticks.More", "Extras.WebController.More" } };
            writer = new StringWriter();
            serializer.Serialize(writer, some);
            Assert.Contains("<Card>Pad.Sticks.More</Card>", writer.ToString(), StringComparison.Ordinal);
            var back = (AppSettingsData)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.Equal(some.OpenCards, back.OpenCards);
        }

        /// <summary>The load and the save each go through the set. A field
        /// the builder drops is the failure a round trip of the data class
        /// alone cannot see.</summary>
        [Fact]
        public void TheSettingsService_LoadsAndSavesTheSet()
        {
            string service = RepoText("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("CardFoldState.Load(appSettings.OpenCards);", service, StringComparison.Ordinal);
            Assert.Contains("OpenCards = CardFoldState.Snapshot(),", service, StringComparison.Ordinal);

            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("CardFoldState.Changed += (_, _) => _settingsService.MarkDirty();", window, StringComparison.Ordinal);
        }

        // ── the parts ──────────────────────────────────────────────────

        [Fact]
        public void APart_OpensTheWayItsKeyWasLeft_AndStoresWhatTheUserDoes() => RunSta(() =>
        {
            using var count = new ChangeCount();
            CardFoldState.Load(new[] { "card" });

            var card = new FoldCard { StateKey = "card" };
            Assert.True(card.IsExpanded);
            var more = new MoreRow { StateKey = "more" };
            Assert.False(more.IsExpanded);
            // Reading the stored state is not a change.
            Assert.Equal(0, count.Changes);

            card.IsExpanded = false;
            Assert.False(CardFoldState.IsOpen("card"));
            more.IsExpanded = true;
            Assert.True(CardFoldState.IsOpen("more"));
            Assert.Equal(2, count.Changes);
        });

        [Fact]
        public void APartOnScreen_FollowsAReplacedSet() => RunSta(() =>
        {
            using var count = new ChangeCount();
            var card = new FoldCard { StateKey = "card" };
            var more = new MoreRow { StateKey = "more", IsExpanded = true };
            Assert.True(CardFoldState.IsOpen("more"));
            int afterSetup = count.Changes;

            // A settings file with one card open, the other not: a load, or
            // a reset to defaults.
            CardFoldState.Load(new[] { "card" });
            Assert.True(card.IsExpanded);
            Assert.False(more.IsExpanded);
            Assert.True(CardFoldState.IsOpen("card"));
            Assert.False(CardFoldState.IsOpen("more"));
            // Following the file is not an edit of it.
            Assert.Equal(afterSetup, count.Changes);
        });

        [Fact]
        public void APartWithNoKey_OpensClosed_AndIsNotStored() => RunSta(() =>
        {
            using var count = new ChangeCount();
            var card = new FoldCard();
            Assert.False(card.IsExpanded);
            card.IsExpanded = true;
            Assert.Empty(CardFoldState.Snapshot());
            Assert.Equal(0, count.Changes);
        });

        // ── the looks ──────────────────────────────────────────────────

        /// <summary>The styles as the app ships them, built into real
        /// controls: the markup from Resources/ControllerIcons.xaml, with
        /// stand-ins for the four resources it takes from elsewhere. A
        /// template that does not parse, or a binding of the wrong type,
        /// fails here and not at the first page that shows a part.</summary>
        private static System.Windows.ResourceDictionary ShippedStyles()
        {
            string file = RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            int from = file.IndexOf("<Style x:Key=\"FoldHeaderToggle\"", StringComparison.Ordinal);
            int to = file.LastIndexOf("</Style>", StringComparison.Ordinal) + "</Style>".Length;
            Assert.True(from > 0 && to > from);
            string assembly = typeof(FoldPart).Assembly.GetName().Name;
            string markup =
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                + " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\""
                + " xmlns:controls=\"clr-namespace:PadForge.Controls;assembly=" + assembly + "\">"
                + "<FontFamily x:Key=\"DisplayFontFamily\">Segoe UI</FontFamily>"
                + "<FontFamily x:Key=\"BodyFontFamily\">Segoe UI</FontFamily>"
                + "<Style x:Key=\"CardBorder\" TargetType=\"Border\"/>"
                + file.Substring(from, to - from)
                + "</ResourceDictionary>";
            return (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(markup);
        }

        [Fact]
        public void TheParts_BuildFromTheirShippedTemplates_AndTheHeaderOpensThem() => RunSta(() =>
        {
            using var count = new ChangeCount();
            var styles = ShippedStyles();

            foreach (FoldPart part in new FoldPart[]
            {
                new FoldCard { Header = "SOCD", Hint = "Which press a game sees", Summary = "Off", StateKey = "test.card" },
                new MoreRow { Header = "More", Hint = "Ports and protocols", StateKey = "test.more" },
            })
            {
                part.Resources = styles;
                part.Content = new System.Windows.Controls.TextBlock { Text = "the rest" };
                part.Style = (System.Windows.Style)styles[part.GetType()];
                Assert.True(part.ApplyTemplate(), part.GetType().Name + " has no template");

                var header = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(part.Template.FindName("HeaderSite", part));
                var content = Assert.IsAssignableFrom<System.Windows.FrameworkElement>(part.Template.FindName("ExpandSite", part));
                Assert.False(part.IsExpanded);
                Assert.False(header.IsChecked);
                Assert.Equal(System.Windows.Visibility.Collapsed, content.Visibility);
                Assert.Equal((string)part.Header, System.Windows.Automation.AutomationProperties.GetName(header));
                Assert.Equal(part.Hint, System.Windows.Automation.AutomationProperties.GetHelpText(header));

                // The header is the control a pointer or a keyboard works.
                header.IsChecked = true;
                Assert.True(part.IsExpanded);
                Assert.Equal(System.Windows.Visibility.Visible, content.Visibility);
                Assert.True(CardFoldState.IsOpen(part.StateKey));

                // And the part's own state moves the header, for a screen
                // reader's expand and collapse.
                part.IsExpanded = false;
                Assert.False(header.IsChecked);
                Assert.Equal(System.Windows.Visibility.Collapsed, content.Visibility);
                Assert.False(CardFoldState.IsOpen(part.StateKey));
            }
        });

        /// <summary>Both parts are Expanders whose header button is bound to
        /// IsExpanded both ways, with the content collapsed until the part
        /// is open.</summary>
        [Theory]
        [InlineData("FoldCard")]
        [InlineData("MoreRow")]
        public void APartsHeader_OpensAndClosesIt(string part)
        {
            Assert.True(typeof(System.Windows.Controls.Expander).IsAssignableFrom(
                typeof(FoldPart).Assembly.GetType("PadForge.Controls." + part)));

            var styles = XDocument.Parse(RepoText("PadForge.App", "Resources", "ControllerIcons.xaml"));
            var style = Assert.Single(styles.Descendants(),
                e => e.Name.LocalName == "Style" && (string)e.Attribute("TargetType") == "{x:Type controls:" + part + "}");
            var header = Assert.Single(style.Descendants(), e => (string)e.Attribute(Xaml + "Name") == "HeaderSite");
            Assert.Equal("ToggleButton", header.Name.LocalName);
            Assert.Equal("{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}",
                (string)header.Attribute("IsChecked"));
            var content = Assert.Single(style.Descendants(), e => (string)e.Attribute(Xaml + "Name") == "ExpandSite");
            Assert.Equal("Collapsed", (string)content.Attribute("Visibility"));
            var open = Assert.Single(style.Descendants(),
                e => e.Name.LocalName == "Trigger" && (string)e.Attribute("Property") == "IsExpanded");
            Assert.Equal("True", (string)open.Attribute("Value"));
            Assert.Contains(open.Elements(), e => (string)e.Attribute("TargetName") == "ExpandSite"
                && (string)e.Attribute("Property") == "Visibility" && (string)e.Attribute("Value") == "Visible");
        }

        /// <summary>A reset button rests with no ring and a dim glyph, and
        /// takes both back under the pointer or keyboard focus. Every reset
        /// style in the app is based on that one style.</summary>
        [Fact]
        public void ResetButtons_RestQuiet_AndComeForwardUnderThePointerOrTheKeyboard()
        {
            var app = XDocument.Parse(RepoText("PadForge.App", "App.xaml"));
            XElement Style(XDocument doc, string key) => Assert.Single(doc.Descendants(),
                e => e.Name.LocalName == "Style" && (string)e.Attribute(Xaml + "Key") == key);
            string Setter(XElement scope, string property) => (string)Assert.Single(scope.Elements(),
                e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == property).Attribute("Value");

            var quiet = Style(app, "QuietResetButton");
            Assert.Equal("{StaticResource EmberIconButton}", (string)quiet.Attribute("BasedOn"));
            Assert.Equal("Transparent", Setter(quiet, "BorderBrush"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", Setter(quiet, "Foreground"));

            var triggers = quiet.Descendants().Where(e => e.Name.LocalName == "Trigger").ToList();
            var pointer = Assert.Single(triggers, t => (string)t.Attribute("Property") == "IsMouseOver");
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", Setter(pointer, "Foreground"));
            var keyboard = Assert.Single(triggers, t => (string)t.Attribute("Property") == "IsKeyboardFocused");
            Assert.Equal("{DynamicResource ColdBrush}", Setter(keyboard, "BorderBrush"));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", Setter(keyboard, "Foreground"));

            Assert.Equal("{StaticResource QuietResetButton}", (string)Style(app, "SettingResetButton").Attribute("BasedOn"));
            var pad = XDocument.Parse(RepoText("PadForge.App", "Views", "PadPage.xaml"));
            Assert.Equal("{StaticResource QuietResetButton}", (string)Style(pad, "ResetButton").Attribute("BasedOn"));
            Assert.Equal("{StaticResource QuietResetButton}", (string)Style(pad, "ResetAllButton").Attribute("BasedOn"));
            Assert.Equal("{StaticResource ResetButton}", (string)Style(pad, "ResetButtonTight").Attribute("BasedOn"));
        }

        /// <summary>No view styles a reset button as a plain icon button,
        /// which would bring the ring back for that one.</summary>
        [Fact]
        public void NoResetButton_WearsThePlainIconButtonStyle()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            string app = Path.Combine(dir.FullName, "PadForge.App");
            var files = Directory.GetFiles(Path.Combine(app, "Views"), "*.xaml").Append(Path.Combine(app, "MainWindow.xaml"));

            var loud = new List<string>();
            foreach (string file in files)
            {
                var doc = XDocument.Parse(File.ReadAllText(file));
                foreach (var button in doc.Descendants().Where(e => (string)e.Attribute("Style") == "{StaticResource EmberIconButton}"))
                {
                    string action = ((string)button.Attribute("Click") ?? "") + " " + ((string)button.Attribute("Command") ?? "");
                    if (action.Contains("Reset", StringComparison.Ordinal))
                        loud.Add(Path.GetFileName(file) + ": " + action.Trim());
                }
            }
            Assert.Empty(loud);
        }
    }
}
