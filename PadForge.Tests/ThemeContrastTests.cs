using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using Wpf.Ui.Appearance;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The three colors that carry meaning are text as well as light (#574):
    /// gold for Low beside a battery, Awaiting devices and the unsaved
    /// changes line, ember for a status line and a count, cold blue for a
    /// measured number. Each holds the contrast text needs in both themes.
    /// One gold served both themes and read about 1.8 to 1 on white, and
    /// thirteen lines of words took the raw ember, which reads about 2.9 to
    /// 1 there. The light theme deepens all three.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ThemeContrastTests
    {
        private static double Channel(byte value)
        {
            double s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        private static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

        /// <summary>The contrast of two opaque colors, as WCAG 2 counts it.</summary>
        internal static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>A color with some alpha as it shows over an opaque one.</summary>
        internal static Color Over(Color top, Color under)
        {
            double a = top.A / 255.0;
            byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
            return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
        }

        /// <summary>What text needs, as WCAG 2 level AA sets it.</summary>
        private const double TextNeeds = 4.5;

        [Fact]
        public void TheMeasureReadsTheKnownCases()
        {
            Assert.Equal(21.0, Contrast(Colors.Black, Colors.White), 6);
            Assert.Equal(1.0, Contrast(Colors.White, Colors.White), 6);
            Assert.Equal(Color.FromRgb(127, 127, 127), Over(Color.FromArgb(128, 0, 0, 0), Colors.White));
            // The gold both themes had and the raw ember, on white: under
            // what text needs.
            Assert.True(Contrast(Color.FromRgb(0xF2, 0xB8, 0x4B), Colors.White) < 2);
            Assert.True(Contrast(Color.FromRgb(0xFF, 0x6B, 0x2C), Colors.White) < 3);
        }

        [Fact]
        public void GoldEmberAndColdReadAsTextInBothThemes()
        {
            string[] words = { "WaitBrush", "EmberTextBrush", "ColdBrush" };
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                Color Of(string key) => ((SolidColorBrush)app.FindResource(key)).Color;
                try
                {
                    // Dark: the boards' colors, on a card.
                    Assert.Equal(Color.FromRgb(0xF2, 0xB8, 0x4B), Of("WaitBrush"));
                    Assert.Equal(Color.FromRgb(0xFF, 0x6B, 0x2C), Of("EmberTextBrush"));
                    Color card = Of("CardBackgroundFillColorDefaultBrush");
                    foreach (string key in words)
                        Assert.True(Contrast(Of(key), card) >= TextNeeds, key + " on a dark card reads " + Contrast(Of(key), card));

                    PageRenderDump.ApplyTheme(ApplicationTheme.Light);
                    PadForge.Common.EmberTheme.ApplyAccent();
                    // White is the lightest ground a light card can be.
                    foreach (string key in words)
                        Assert.True(Contrast(Of(key), Colors.White) >= TextNeeds, key + " on a white card reads " + Contrast(Of(key), Colors.White));
                    // Gold also stands on its own well and on its own tint.
                    Color gold = Of("WaitBrush");
                    double onWell = Contrast(gold, Of("WaitWellBrush"));
                    double onTint = Contrast(gold, Over(Of("WaitTintBrush"), Colors.White));
                    Assert.True(onWell >= TextNeeds, "gold on the wait well reads " + onWell);
                    Assert.True(onTint >= TextNeeds, "gold on the wait tint reads " + onTint);
                }
                finally
                {
                    // The dark theme back, which every other test stands in.
                    PageRenderDump.ApplyTheme(ApplicationTheme.Dark);
                    PadForge.Common.EmberTheme.ApplyAccent();
                }
            }, TimeSpan.FromMinutes(2), "the theme contrast test timed out");
        }

        private const string RawEmber = "{DynamicResource EmberBrush}";

        /// <summary>Whether a part draws a glyph of an icon font, which is a
        /// mark and no word.</summary>
        private static bool IsGlyph(XElement part)
            => ((string)part.Attribute("FontFamily") ?? string.Empty).Contains("Segoe", StringComparison.Ordinal)
               || ((string)part.Attribute("Style") ?? string.Empty).Contains("Glyph", StringComparison.Ordinal);

        /// <summary>Words in ember take EmberTextBrush, the raw accent in
        /// the dark theme and a deeper ember in the light one. EmberBrush is
        /// the raw accent in both and stays for marks, glyphs and fills.</summary>
        [Fact]
        public void WordsInEmberTakeTheEmberTheLightThemeDeepens()
        {
            string root = AppContext.BaseDirectory;
            while (root != null && !File.Exists(Path.Combine(root, "PadForge.sln"))) root = Path.GetDirectoryName(root);
            Assert.NotNull(root);
            string[] wordTags = { "TextBlock", "Run", "Button", "Hyperlink" };
            var found = new List<string>();
            int read = 0;
            foreach (var (rel, text) in XamlA11yAudit.Files(root))
            {
                var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
                read++;
                foreach (XElement part in doc.Descendants())
                {
                    int line = ((IXmlLineInfo)part).LineNumber;
                    string tag = part.Name.LocalName;
                    if (wordTags.Contains(tag) && (string)part.Attribute("Foreground") == RawEmber && !IsGlyph(part))
                        found.Add(rel + ":" + line + " " + tag);
                    if (tag != "Setter" || (string)part.Attribute("Property") != "Foreground" || (string)part.Attribute("Value") != RawEmber) continue;
                    if (part.Attribute("TargetName") != null) continue;
                    XElement style = part.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Style");
                    if (style == null) continue;
                    string target = (string)style.Attribute("TargetType") ?? string.Empty;
                    bool words = wordTags.Any(w => target == w || target == "{x:Type " + w + "}" || target.EndsWith(":" + w, StringComparison.Ordinal));
                    bool glyph = style.Descendants().Any(s => s.Name.LocalName == "Setter" && (string)s.Attribute("Property") == "FontFamily"
                                                              && ((string)s.Attribute("Value") ?? string.Empty).Contains("Segoe", StringComparison.Ordinal))
                                 || ((string)style.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key") ?? string.Empty).Contains("Glyph", StringComparison.Ordinal)
                                 || IsGlyph(style.Parent?.Parent ?? style);
                    if (words && !glyph) found.Add(rel + ":" + line + " a style of " + target);
                }
            }
            Assert.True(read > 40, "only " + read + " files were read");
            Assert.True(found.Count == 0, "words in the raw ember:\n" + string.Join("\n", found));
        }
    }
}
