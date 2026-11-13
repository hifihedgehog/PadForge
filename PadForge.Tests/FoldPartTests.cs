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

            // A change is saved as interface state, which the autosave
            // writes without the saved message (ViewStateSaveTests).
            string window = RepoText("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("CardFoldState.Changed += (_, _) => _settingsService.MarkViewStateDirty();", window, StringComparison.Ordinal);
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

        /// <summary>The Settings page's search opens a part whose rows match
        /// and closes it after (#574). Neither is the user's doing, so neither
        /// is stored, and the end of the search puts back what was.</summary>
        [Fact]
        public void FoldPart_ShowForSearch_StoresNothing() => RunSta(() =>
        {
            using var count = new ChangeCount();
            CardFoldState.Load(new[] { "stored" });
            var stored = new FoldCard { StateKey = "stored" };
            var loose = new MoreRow { StateKey = "loose" };
            var keyless = new MoreRow();
            Assert.True(stored.IsExpanded);

            loose.ShowForSearch(true);
            keyless.ShowForSearch(true);
            stored.ShowForSearch(false);
            Assert.True(loose.IsExpanded);
            Assert.True(keyless.IsExpanded);
            Assert.False(stored.IsExpanded);
            Assert.False(CardFoldState.IsOpen("loose"));
            Assert.True(CardFoldState.IsOpen("stored"));
            Assert.Equal(0, count.Changes);

            loose.EndSearch();
            keyless.EndSearch();
            stored.EndSearch();
            Assert.False(loose.IsExpanded);
            Assert.False(keyless.IsExpanded);
            Assert.True(stored.IsExpanded);
            Assert.Equal(0, count.Changes);
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
            // The fold parts end where the Extras parts begin (ExtrasPartsTests builds those).
            int next = file.IndexOf("Extras parts (#574)", StringComparison.Ordinal);
            Assert.True(from > 0 && next > from);
            int to = file.LastIndexOf("</Style>", next, StringComparison.Ordinal) + "</Style>".Length;
            Assert.True(to > from);
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

        /// <summary>A closed line ends with its value at the line's end,
        /// before the part was ever opened and after it was opened and
        /// closed. The room for the open part's action was bound to the
        /// action's width, and an action that collapses reports no change
        /// of width, so the room stayed and the value stood the action's
        /// width short of the end (seen 2026-11-11 on Motion Steering and
        /// Gyro Tilt beside parts that had never been opened).</summary>
        [Fact]
        public void AClosedLinesValue_StandsAtTheEnd_AfterThePartWasOpenedToo() => RunSta(() =>
        {
            using var count = new ChangeCount();
            var styles = ShippedStyles();
            var card = new FoldCard
            {
                Header = "Gyro Tilt",
                Hint = "Full Tilt Range, Tilt Deadzone",
                Summary = "25",
                HeaderAction = new System.Windows.Controls.Border { Width = 80, Height = 30 },
                Resources = styles,
                Content = new System.Windows.Controls.TextBlock { Text = "the rows" },
            };
            card.Style = (System.Windows.Style)styles[typeof(FoldCard)];
            Assert.True(card.ApplyTemplate());
            var value = Assert.IsType<System.Windows.Controls.TextBlock>(card.Template.FindName("SummaryText", card));
            var action = Assert.IsAssignableFrom<System.Windows.FrameworkElement>(card.Template.FindName("ActionSite", card));

            void Layout()
            {
                // Twice: a width that follows another element's settles on
                // the pass after that element's.
                for (int pass = 0; pass < 2; pass++)
                {
                    card.Measure(new System.Windows.Size(600, double.PositiveInfinity));
                    card.Arrange(new System.Windows.Rect(0, 0, 600, card.DesiredSize.Height));
                    card.UpdateLayout();
                }
            }
            double ValueEnd() => value.TranslatePoint(new System.Windows.Point(value.RenderSize.Width, 0), card).X;

            Layout();
            double closed = ValueEnd();
            // The line's padding at its end is 16.
            Assert.Equal(600 - 16, closed, 3);

            card.IsExpanded = true;
            Layout();
            // Open, the action stands at the end and the value is away.
            Assert.Equal(System.Windows.Visibility.Visible, action.Visibility);
            Assert.Equal(System.Windows.Visibility.Collapsed, value.Visibility);
            Assert.Equal(80, action.RenderSize.Width, 3);

            card.IsExpanded = false;
            Layout();
            Assert.Equal(System.Windows.Visibility.Visible, value.Visibility);
            Assert.Equal(closed, ValueEnd(), 3);
        });

        /// <summary>Both parts are Expanders whose header button is bound to
        /// IsExpanded both ways, with the content collapsed until the part
        /// is open. The More card of the Extras pages (MoreCardRow) is a More
        /// row drawn as a card of its own, so it holds the same.</summary>
        [Theory]
        [InlineData("FoldCard", null)]
        [InlineData("MoreRow", null)]
        [InlineData("MoreRow", "MoreCardRow")]
        public void APartsHeader_OpensAndClosesIt(string part, string key)
        {
            Assert.True(typeof(System.Windows.Controls.Expander).IsAssignableFrom(
                typeof(FoldPart).Assembly.GetType("PadForge.Controls." + part)));

            var styles = XDocument.Parse(RepoText("PadForge.App", "Resources", "ControllerIcons.xaml"));
            var style = Assert.Single(styles.Descendants(),
                e => e.Name.LocalName == "Style" && (string)e.Attribute("TargetType") == "{x:Type controls:" + part + "}"
                    && (string)e.Attribute(Xaml + "Key") == key);
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

            // The part hands the page's text color to what it holds. A
            // control's Foreground starts at the system's black, so a title
            // inside with no color of its own (the Head Tracking page's
            // Per-Axis Range, 2026-11-10) drew black on the dark card. The
            // More card takes it from the More row's style it is based on.
            if (key != null)
            {
                Assert.Equal("{StaticResource {x:Type controls:" + part + "}}", (string)style.Attribute("BasedOn"));
                Assert.DoesNotContain(style.Elements(), e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == "Foreground");
                return;
            }
            var color = Assert.Single(style.Elements(), e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == "Foreground");
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", (string)color.Attribute("Value"));
        }

        /// <summary>The More card of the Extras pages, built from its shipped
        /// style: a card of its own in a FoldList, so it takes back no card
        /// padding, its line is 48 tall with no hairline over it, the rest
        /// opens under a hairline with the board's padding, and opening it
        /// is stored under its key like any More row.</summary>
        [Fact]
        public void TheMoreCard_BuildsFromItsShippedStyle() => RunSta(() =>
        {
            using var count = new ChangeCount();
            var styles = ShippedStyles();
            var more = new MoreRow { Header = "More", Hint = "Port", StateKey = "test.morecard", Resources = styles };
            more.Content = new System.Windows.Controls.TextBlock { Text = "the rest" };
            more.Style = (System.Windows.Style)styles["MoreCardRow"];
            Assert.True(more.ApplyTemplate(), "the More card has no template");
            Assert.Equal(new System.Windows.Thickness(0), more.Margin);

            var header = Assert.IsType<System.Windows.Controls.Primitives.ToggleButton>(more.Template.FindName("HeaderSite", more));
            Assert.Equal(48, header.MinHeight);
            Assert.Equal(new System.Windows.Thickness(20, 0, 20, 0), header.Padding);
            Assert.Same(styles["FoldHeaderToggle"], header.Style);
            var site = Assert.IsType<System.Windows.Controls.Border>(more.Template.FindName("ExpandSite", more));
            Assert.Equal(new System.Windows.Thickness(0, 1, 0, 0), site.BorderThickness);
            Assert.Equal(new System.Windows.Thickness(20, 6, 12, 12), site.Padding);
            Assert.Equal(System.Windows.Visibility.Collapsed, site.Visibility);

            header.IsChecked = true;
            Assert.True(more.IsExpanded);
            Assert.Equal(System.Windows.Visibility.Visible, site.Visibility);
            Assert.True(CardFoldState.IsOpen("test.morecard"));
            Assert.Equal(1, count.Changes);
            more.IsExpanded = false;
            Assert.False(CardFoldState.IsOpen("test.morecard"));
        });

        /// <summary>A folded part's value takes the part's SummaryForeground,
        /// which the style sets to the secondary text color: every fold keeps
        /// its look, and a list that writes a count there (the Devices page's
        /// folds) sets the tertiary one.</summary>
        [Fact]
        public void AFoldsValue_TakesItsPartsColor()
        {
            var styles = XDocument.Parse(RepoText("PadForge.App", "Resources", "ControllerIcons.xaml"));
            var style = Assert.Single(styles.Descendants(),
                e => e.Name.LocalName == "Style" && (string)e.Attribute("TargetType") == "{x:Type controls:FoldCard}");
            var setter = Assert.Single(style.Elements(), e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == "SummaryForeground");
            Assert.Equal("{DynamicResource TextFillColorSecondaryBrush}", (string)setter.Attribute("Value"));
            var value = Assert.Single(style.Descendants(), e => (string)e.Attribute(Xaml + "Name") == "SummaryText");
            Assert.Equal("{TemplateBinding SummaryForeground}", (string)value.Attribute("Foreground"));
        }

        /// <summary>A quiet button has no fill and no edge, and none while
        /// it cannot be used: the two keys the button template fills and
        /// rings a disabled button from are shadowed with nothing. Left to
        /// the theme they drew a box around Duplicate and Remove under a
        /// macro's steps while no step was picked (seen 2026-11-12).</summary>
        [Fact]
        public void AQuietButton_HasNoFillAndNoEdge_AlsoWhileItCannotBeUsed()
        {
            var app = XDocument.Parse(RepoText("PadForge.App", "App.xaml"));
            var quiet = Assert.Single(app.Descendants(), e => e.Name.LocalName == "Style" && (string)e.Attribute(Xaml + "Key") == "QuietButton");
            string Setter(string property) => (string)Assert.Single(quiet.Elements(),
                e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == property).Attribute("Value");
            Assert.Equal("Transparent", Setter("Background"));
            Assert.Equal("Transparent", Setter("BorderBrush"));
            Assert.Equal("{StaticResource PadUiButtonTemplate}", Setter("Template"));
            var shadowed = quiet.Elements().Single(e => e.Name.LocalName == "Style.Resources").Elements().ToList();
            Assert.Equal(new[] { "ButtonBackgroundDisabled", "ButtonBorderBrushDisabled" }, shadowed.Select(e => (string)e.Attribute(Xaml + "Key")));
            Assert.All(shadowed, e => Assert.Equal("Transparent", (string)e.Attribute("Color")));
        }

        /// <summary>A quiet plain button has no fill and no edge while it
        /// cannot be used, as a quiet button has none. Left to the theme, a
        /// Bass Shakers tone's Test and the Stop beside the sweep drew a
        /// box while the shakers were off (2026-11-12). Under the pointer
        /// and pressed it still has no edge.</summary>
        [Fact]
        public void AQuietPlainButton_HasNoFillAndNoEdge_AlsoWhileItCannotBeUsed()
        {
            var app = XDocument.Parse(RepoText("PadForge.App", "App.xaml"));
            var quiet = Assert.Single(app.Descendants(), e => e.Name.LocalName == "Style" && (string)e.Attribute(Xaml + "Key") == "QuietPlainButton");
            Assert.Equal("{StaticResource DefaultButtonStyle}", (string)quiet.Attribute("BasedOn"));
            string Setter(string property) => (string)Assert.Single(quiet.Elements(),
                e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == property).Attribute("Value");
            Assert.Equal("Transparent", Setter("Background"));
            Assert.Equal("Transparent", Setter("BorderBrush"));
            var shadowed = quiet.Elements().Single(e => e.Name.LocalName == "Style.Resources").Elements().ToList();
            Assert.Equal(new[] { "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBackgroundDisabled", "ButtonBorderBrushDisabled" },
                shadowed.Select(e => (string)e.Attribute(Xaml + "Key")));
            Assert.All(shadowed, e => Assert.Equal("Transparent", (string)e.Attribute("Color")));
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
            Assert.Equal("{DynamicResource QuietGlyphBrush}", Setter(quiet, "Foreground"));
            // 32 square with no margin: the board's size, and the same 32 a
            // row paid for the icon button's 28 and its 4 of margin.
            Assert.Equal("32", Setter(quiet, "Width"));
            Assert.Equal("32", Setter(quiet, "Height"));
            Assert.Equal("0", Setter(quiet, "Margin"));
            // A reset that rests with its row is its dimmed glyph alone:
            // the two keys the button template fills and rings a disabled
            // button from are shadowed with nothing.
            var shadowed = quiet.Elements().Single(e => e.Name.LocalName == "Style.Resources").Elements().ToList();
            Assert.Equal(new[] { "ButtonBackgroundDisabled", "ButtonBorderBrushDisabled" }, shadowed.Select(e => (string)e.Attribute(Xaml + "Key")));
            Assert.All(shadowed, e => Assert.Equal("Transparent", (string)e.Attribute("Color")));
            string template = RepoText("PadForge.App", "App.xaml");
            Assert.Contains("Property=\"Background\" Value=\"{DynamicResource ButtonBackgroundDisabled}\"", template);
            Assert.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource ButtonBorderBrushDisabled}\"", template);

            var triggers = quiet.Descendants().Where(e => e.Name.LocalName == "Trigger").ToList();
            var pointer = Assert.Single(triggers, t => (string)t.Attribute("Property") == "IsMouseOver");
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", Setter(pointer, "Foreground"));
            var keyboard = Assert.Single(triggers, t => (string)t.Attribute("Property") == "IsKeyboardFocused");
            Assert.Equal("{DynamicResource ColdBrush}", Setter(keyboard, "BorderBrush"));
            Assert.Equal("{DynamicResource TextFillColorPrimaryBrush}", Setter(keyboard, "Foreground"));

            Assert.Equal("{StaticResource QuietResetButton}", (string)Style(app, "SettingResetButton").Attribute("BasedOn"));
            var pad = XDocument.Parse(RepoText("PadForge.App", "Views", "PadPage.xaml"));
            Assert.Equal("{StaticResource QuietResetButton}", (string)Style(pad, "ResetButton").Attribute("BasedOn"));
            // Reset All is shared by the controller page, the Extras pages
            // and the Devices page, so it lives in App.xaml.
            Assert.Equal("{StaticResource QuietResetButton}", (string)Style(app, "ResetAllButton").Attribute("BasedOn"));
            Assert.DoesNotContain(pad.Descendants(), e => (string)e.Attribute(Xaml + "Key") == "ResetAllButton");
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
