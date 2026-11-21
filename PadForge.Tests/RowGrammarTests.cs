using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// How the controller page draws one setting's row, held across the whole
    /// page: where a row's label stands and where its control starts. Each
    /// rule here was broken somewhere on the page in the 6.0.0 screenshots.
    /// </summary>
    public class RowGrammarTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static XDocument Page()
            => XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));

        private static bool IsCard(XElement e)
            => e.Name.LocalName == "FoldCard" || (string)e.Attribute("Style") == "{StaticResource CardBorder}";

        /// <summary>A card's own rows in the order the page draws them, and
        /// none of a card inside it.</summary>
        private static IEnumerable<XElement> RowsOf(XElement card)
            => card.Descendants().Where(e => e.Name.LocalName == "SettingRow" && e.Ancestors().First(IsCard) == card);

        private static string LabelOf(XElement row)
            => (string)row.Elements().FirstOrDefault(e => e.Name.LocalName == "TextBlock")?.Attribute("Text") ?? "(no label)";

        private static bool IsIndented(XElement row)
            => row.Elements().Any(e => e.Name.LocalName == "TextBlock" && (string)e.Attribute("Style") == "{StaticResource DzSubLabel}");

        /// <summary>An indented row is a setting that obeys a row above it:
        /// Radial Zone Count under Radial Zones, D-Pad Activation under D-Pad
        /// Mode. A card's own switch stands in its head, so a row that obeys
        /// only that switch stands at the card's label column, as Audio
        /// Rumble's rows do. Six rows kept the indent of a switch row the
        /// sections pass moved into their card's head, and stood indented
        /// under nothing (seen in the 6.0.0 screenshots: Max Radius and Inner
        /// Deadzone in from D-Pad Mode, and the EQ's Preamp in from its band
        /// table).</summary>
        [Fact]
        public void AnIndentedRowHasARowOfItsCardAboveIt()
        {
            var page = Page();
            var cards = page.Descendants().Where(IsCard).ToList();
            Assert.True(cards.Count > 40, cards.Count + " cards");
            var alone = new List<string>();
            foreach (var card in cards)
            {
                bool rowAbove = false;
                foreach (var row in RowsOf(card))
                {
                    if (!IsIndented(row)) rowAbove = true;
                    else if (!rowAbove) alone.Add(LabelOf(row));
                }
            }
            Assert.Empty(alone);
        }
    }
}
