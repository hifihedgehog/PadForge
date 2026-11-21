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

        private static bool IsEndsRow(XElement row)
            => row.AncestorsAndSelf().Any(a => a.Attributes().Any(t => t.Name.LocalName == "SettingRow.Ends" && t.Value == "True"));

        /// <summary>An on/off setting's row ends with its switch beside its
        /// reset, and its name takes the room before them, as the boards draw
        /// every switch row. Re-Center When Gyro Turns On had its switch in the
        /// pickers' column, so its name had the label column alone and "On"
        /// stood on a line of its own (seen in the 6.0.0 screenshots). The
        /// Extended card keeps its label column beside its number boxes, as it
        /// was built: it is on no board.</summary>
        [Fact]
        public void ASwitchRowEndsWithItsSwitch()
        {
            var page = Page();
            var extended = page.Descendants().Single(e => (string)e.Attribute(X + "Name") == "ExtendedConfigBar");
            var rows = page.Descendants().Where(e => e.Name.LocalName == "SettingRow"
                && e.Elements().Any(c => c.Name.LocalName == "CheckBox" && (string)c.Attribute("Style") == "{StaticResource SwitchCheckBox}")).ToList();
            Assert.True(rows.Count > 30, rows.Count + " switch rows");
            Assert.Empty(rows.Where(r => !IsEndsRow(r) && !r.Ancestors().Contains(extended)).Select(LabelOf));

            var recenter = rows.Single(r => LabelOf(r).StartsWith("{Binding Pad_GyroRecenterOnEngage,", System.StringComparison.Ordinal));
            Assert.Equal("{StaticResource EndsLabel}", (string)recenter.Elements().First().Attribute("Style"));
        }
    }
}
