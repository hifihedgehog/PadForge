using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Latin name and its bracketed note in a right-to-left paragraph
    /// (#505). WPF has no bracket-pair rule, so in "Igor Budzhak (MIT)"
    /// followed by Arabic the closing bracket took the paragraph's direction
    /// and was drawn mirrored on the far side of the name. Drawn unwrapped,
    /// the translations carried 96 such pairs in Arabic, 41 in Persian, 111 in
    /// Urdu and 133 in Central Kurdish. BidiText puts each Latin stretch that
    /// holds a bracket pair into a left-to-right Run.
    /// </summary>
    public class BidiTextTests
    {
        // Letters, not words: right-to-left text on either side of the credit.
        private const string Before = "ابت ";
        private const string After = " ثجح";
        private const string Credit = "Igor Budzhak (MIT)";

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        private static string RepoText(params string[] parts)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return File.ReadAllText(Path.Combine(new[] { d.FullName }.Concat(parts).ToArray()));
        }

        private static string[] Wrapped(string text)
            => BidiText.Split(text).Where(p => p.LeftToRight).Select(p => p.Text).ToArray();

        // ─────────────────────────────────────────────
        //  Which stretches go into a left-to-right Run
        // ─────────────────────────────────────────────

        [Fact]
        public void TheCreditBetweenRightToLeftWordsIsWrappedWhole()
        {
            Assert.Equal(new[] { Credit }, Wrapped(Before + Credit + After));
            Assert.Equal(Before + Credit + After, string.Concat(BidiText.Split(Before + Credit + After).Select(p => p.Text)));
        }

        /// <summary>Latin credits joined by the Arabic comma are one stretch,
        /// as Unicode's own algorithm reads them, and the trailing comma stays
        /// with the right-to-left text.</summary>
        [Fact]
        public void LatinCreditsJoinedByTheArabicCommaAreOneStretch()
        {
            Assert.Equal(new[] { "antifier (MIT)، chmfp (GPL-3.0-or-later)" },
                Wrapped(Before + "antifier (MIT)، chmfp (GPL-3.0-or-later)،" + After));
        }

        [Fact]
        public void AnAllLatinSignatureIsWrappedWhole()
        {
            Assert.Equal(new[] { "clamp(value, low, high)" }, Wrapped("clamp(value, low, high)"));
        }

        /// <summary>A bracket that encloses right-to-left text takes the
        /// paragraph's direction on both sides and is drawn correctly, so it is
        /// left alone. Latin brackets inside it are still wrapped.</summary>
        [Fact]
        public void BracketsAroundRightToLeftTextAreLeftToTheParagraph()
        {
            Assert.Empty(Wrapped("ابت (api.github.com ثجح)" + After));
            Assert.Equal(new[] { "s[0]، s[1]" }, Wrapped("ابت (ثجح s[0]، s[1]، …)" + After));
            Assert.Equal(new[] { "acer-predator-helios-rgb (GPL-2.0-only)" },
                Wrapped("(ثجح MIT)، acer-predator-helios-rgb (GPL-2.0-only)" + After));
        }

        [Fact]
        public void TextWithoutABracketPairIsOnePlainPiece()
        {
            Assert.Empty(BidiText.Split(null));
            Assert.Empty(BidiText.Split(string.Empty));
            var pieces = BidiText.Split(Before + "PadForge" + After);
            Assert.Single(pieces);
            Assert.False(pieces[0].LeftToRight);
        }

        // ─────────────────────────────────────────────
        //  What WPF draws
        // ─────────────────────────────────────────────

        /// <summary>The leading edge of the character at <paramref name="index"/>,
        /// whichever Run holds it. Under RightToLeft the element's x grows
        /// leftward.</summary>
        private static double Edge(TextBlock block, int index)
        {
            TextPointer p = block.ContentStart;
            int seen = 0;
            while (p != null)
            {
                if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    int length = p.GetTextRunLength(LogicalDirection.Forward);
                    if (index < seen + length)
                        return p.GetPositionAtOffset(index - seen).GetCharacterRect(LogicalDirection.Forward).X;
                    seen += length;
                    p = p.GetPositionAtOffset(length);
                }
                else
                    p = p.GetNextContextPosition(LogicalDirection.Forward);
            }
            throw new InvalidOperationException("index " + index + " is past the text");
        }

        private static TextBlock RightToLeft()
            => new TextBlock { FlowDirection = FlowDirection.RightToLeft, Language = XmlLanguage.GetLanguage("ar"), FontSize = 13 };

        private static void Lay(TextBlock block)
        {
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            block.Arrange(new Rect(block.DesiredSize));
            block.UpdateLayout();
        }

        /// <summary>Through BidiText the closing bracket is drawn after
        /// "MIT", inside the name. The plain Text beside it is the positive
        /// control: WPF still draws that bracket on the far side of the name,
        /// so the test can tell the two apart.</summary>
        [Fact]
        public void TheClosingBracketStaysWithItsName()
        {
            RunSta(() =>
            {
                string text = Before + Credit + After;
                int name = text.IndexOf("Igor", StringComparison.Ordinal);
                int open = text.IndexOf('(');
                int content = text.IndexOf("MIT", StringComparison.Ordinal);
                int close = text.IndexOf(')');

                var fixedBlock = RightToLeft();
                BidiText.SetText(fixedBlock, text);
                Lay(fixedBlock);
                Assert.True(Edge(fixedBlock, name) > Edge(fixedBlock, open)
                            && Edge(fixedBlock, open) > Edge(fixedBlock, content)
                            && Edge(fixedBlock, content) > Edge(fixedBlock, close),
                    "through BidiText the credit reads Igor Budzhak (MIT) left to right");

                var plain = RightToLeft();
                plain.Text = text;
                Lay(plain);
                Assert.True(Edge(plain, close) > Edge(plain, name),
                    "plain Text no longer draws the closing bracket on the far side of the name: WPF may have gained the bracket-pair rule");
            });
        }

        [Fact]
        public void TextWithNothingToWrapStaysPlainText()
        {
            RunSta(() =>
            {
                var block = new TextBlock();
                BidiText.SetText(block, Before + "PadForge" + After);
                Assert.Equal(Before + "PadForge" + After, block.Text);
                Assert.Single(block.Inlines);

                BidiText.SetText(block, Before + Credit + After);
                var runs = block.Inlines.OfType<Run>().ToArray();
                Assert.Equal(3, runs.Length);
                Assert.Equal(Credit, runs[1].Text);
                Assert.Equal(FlowDirection.LeftToRight, runs[1].FlowDirection);

                // A language switch sets the property again.
                BidiText.SetText(block, "PadForge");
                Assert.Equal("PadForge", block.Text);
                Assert.Single(block.Inlines);
            });
        }

        [Fact]
        public void TheToolTipIsATextBlockCarryingTheText()
        {
            RunSta(() =>
            {
                var button = new Button();
                BidiText.SetToolTip(button, "clamp(value, low, high)" + After);
                var tip = Assert.IsType<TextBlock>(button.ToolTip);
                Assert.Equal("clamp(value, low, high)" + After, BidiText.GetText(tip));

                BidiText.SetToolTip(button, "sin(angle)");
                Assert.Same(tip, button.ToolTip);
                Assert.Equal("sin(angle)", BidiText.GetText(tip));

                BidiText.SetToolTip(button, null);
                Assert.Null(button.ToolTip);
            });
        }

        // ─────────────────────────────────────────────
        //  Where the markup uses it
        // ─────────────────────────────────────────────

        /// <summary>Every string on the About page goes through BidiText: the
        /// credits are where nearly all of the broken pairs were.</summary>
        [Fact]
        public void EveryAboutPageStringGoesThroughBidiText()
        {
            string page = RepoText("PadForge.App", "Views", "AboutPage.xaml");
            Assert.True(Regex.Matches(page, @"common:BidiText\.Text=""\{Binding About_").Count > 250);
            Assert.DoesNotMatch(@"\sText=""\{Binding About_", page);
        }

        /// <summary>The pad page strings whose translations held a broken pair,
        /// and every formula chip's tooltip.</summary>
        [Fact]
        public void ThePadPageStringsWithBracketsGoThroughBidiText()
        {
            string page = RepoText("PadForge.App", "Views", "PadPage.xaml");
            foreach (string key in new[] { "Macro_Expression_Tooltip", "Macro_KeyComboHint", "MacroAction_ShowDreamcastScreen_Hint" })
                Assert.Contains("common:BidiText.Text=\"{Binding " + key + ",", page);
            // Which controllers Bass Shakers works with is its card title's
            // tooltip since the card lost its paragraph (#574).
            foreach (string key in new[] { "Pad_MidiChannelTooltip", "Pad_MidiStartCCTooltip", "Macro_KeyComboTooltip", "Pad_RumbleAudio_Card_Desc" })
                Assert.Contains("common:BidiText.ToolTip=\"{Binding " + key + ",", page);
            Assert.DoesNotMatch(@"\s(?:Text|ToolTip)=""\{Binding Pad_RumbleAudio_Card_Desc", page);
            Assert.DoesNotMatch(@"\sToolTip=""\{Binding Pad_Formula_Chip_", page);
            Assert.True(Regex.Matches(page, @"common:BidiText\.ToolTip=""\{Binding Pad_Formula_Chip_").Count > 70);
        }
    }
}
