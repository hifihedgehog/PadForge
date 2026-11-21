using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>Where a row's control starts in the section that holds
        /// <paramref name="at"/>: its label column and the room after it.</summary>
        private static double ControlColumn(XElement at)
        {
            var label = at.Ancestors()
                .SelectMany(a => a.Elements().Where(r => r.Name.LocalName.EndsWith(".Resources", System.StringComparison.Ordinal)))
                .SelectMany(r => r.Elements())
                .First(s => s.Name.LocalName == "Style" && (string)s.Attribute(X + "Key") == "DzLabel");
            string Setter(string property) => (string)label.Elements().Single(s => (string)s.Attribute("Property") == property).Attribute("Value");
            return double.Parse(Setter("Width"), CultureInfo.InvariantCulture)
                + double.Parse(Setter("Margin").Split(',')[2], CultureInfo.InvariantCulture);
        }

        /// <summary>A picker of one input is a row like the page's other
        /// pickers: its label at the row's middle in the label column, its box
        /// where the row's control starts, and its record and reset buttons
        /// beside the box. The line that names a picked input's device stands
        /// under the row from where the control starts, as the gyro's
        /// calibration line does. The labels stood 4 from the row's top, above
        /// the box's middle, and the trigger Activators' own margin dropped the
        /// 12 after the label column, so their boxes started 12 left of the
        /// pickers over and under them (seen in the 6.0.0 screenshots).</summary>
        [Fact]
        public void AnInputPickerRowIsARowLikeItsNeighbors()
        {
            var page = Page();
            var pickers = page.Descendants().Where(e => e.Name.LocalName == "ComboBox"
                && (string)e.Attribute("ItemsSource") == "{Binding SlotAvailableInputsView}").ToList();
            Assert.Equal(5, pickers.Count);
            foreach (var picker in pickers)
            {
                string input = ((string)picker.Attribute("SelectedItem")).Split(' ', ',')[1];
                var row = picker.Ancestors().First(a => a.Name.LocalName == "SettingRow");
                var parts = row.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList();
                Assert.Equal("TextBlock", parts[0].Name.LocalName);
                Assert.Null(parts[0].Attribute("VerticalAlignment"));
                Assert.Null(parts[0].Attribute("Margin"));
                // The box and the words in it are the row's control, at its middle.
                Assert.Same(picker.Parent, parts[1]);
                Assert.Equal("Center", (string)parts[1].Attribute("VerticalAlignment"));
                Assert.All(parts.Skip(2), p => Assert.Null(p.Attribute("VerticalAlignment")));
                // The picked input's device, under the row.
                var line = page.Descendants().Single(e => (string)e.Attribute("Text") == "{Binding " + input + ".DeviceLabel}");
                Assert.Same(row, line.ElementsBeforeSelf().Last());
                Assert.Equal(ControlColumn(row), double.Parse(((string)line.Attribute("Margin")).Split(',')[0], CultureInfo.InvariantCulture));
            }
        }

        /// <summary>A number box that is a row's first control starts where
        /// the row's control does, as a picker or a slider there does, and as
        /// Wheel Torque's always has. The value style sets the box 10 after a
        /// slider, and a box with no slider before it kept that 10, so Mouse
        /// Gestures' Flick Distance and Cooldown stood 10 right of the Custom
        /// Activation Input picker between them (seen in the 6.0.0
        /// screenshots). The Extras page's crosshair Picture Size kept its
        /// style's 8 the same way.</summary>
        [Theory]
        [InlineData("PadPage.xaml", 18)]
        [InlineData("ExtrasPage.xaml", 1)]
        public void ANumberBoxThatOpensARowStartsAtItsColumn(string file, int boxes)
        {
            var page = XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", file));
            var first = page.Descendants().Where(e => e.Name.LocalName == "SettingRow" && !IsEndsRow(e))
                .Select(r => r.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList())
                .Where(parts => parts.Count > 1 && parts[0].Name.LocalName == "TextBlock" && parts[1].Name.LocalName == "TextBox"
                    && (string)parts[1].Attribute("Style") is "{StaticResource DzValueEdit}" or "{StaticResource FieldValueEdit}")
                .Select(parts => (Label: (string)parts[0].Attribute("Text"), Margin: (string)parts[1].Attribute("Margin")))
                .ToList();
            Assert.Equal(boxes, first.Count);
            Assert.Empty(first.Where(b => b.Margin == null || double.Parse(b.Margin.Split(',')[0], CultureInfo.InvariantCulture) != 0)
                .Select(b => b.Label));
        }

        /// <summary>Check boxes that are a row's control start their boxes
        /// where the row's control does. The library's check box sets its box
        /// 11 in from its edge, so the Gesture Buttons' first box stood 11
        /// right of the Custom Activation Input picker under it (seen in the
        /// 6.0.0 screenshots). A list's check boxes stand inside the list's
        /// own frame and keep the inset.</summary>
        [Fact]
        public void CheckBoxesThatAreARowsControlStartAtItsColumn()
        {
            var page = Page();
            var panels = page.Descendants().Where(e => e.Name.LocalName == "SettingRow" && !IsEndsRow(e))
                .Select(r => r.Elements().Where(e => !e.Name.LocalName.Contains('.')).ToList())
                .Where(parts => parts.Count > 1 && parts[0].Name.LocalName == "TextBlock"
                    && parts[1].Name.LocalName is "WrapPanel" or "StackPanel"
                    && parts[1].Elements().Any(c => c.Name.LocalName == "CheckBox" && c.Attribute("Style") == null))
                .Select(parts => parts[1]).ToList();
            var panel = Assert.Single(panels);
            Assert.Contains("Pad_MouseGestures", (string)panel.Parent!.Elements().First().Attribute("Text"));
            var style = Assert.Single(panel.Elements().Single(e => e.Name.LocalName == "WrapPanel.Resources").Elements());
            Assert.Equal("CheckBox", (string)style.Attribute("TargetType"));
            Assert.Equal("{StaticResource {x:Type CheckBox}}", (string)style.Attribute("BasedOn"));
            string padding = (string)style.Elements().Single(s => (string)s.Attribute("Property") == "Padding").Attribute("Value");
            Assert.Equal("0", padding.Split(',')[0]);
            Assert.All(panel.Elements().Where(c => c.Name.LocalName == "CheckBox"), c => Assert.Null(c.Attribute("Padding")));
        }
    }
}
