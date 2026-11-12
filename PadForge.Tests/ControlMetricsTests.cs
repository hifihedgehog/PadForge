using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The metrics the PadForge 6 boards draw every control with,
    /// set once for the whole app (#574): corners of 6, a picker 34 tall with
    /// its words in the middle of the box, a plain button 36 tall with 14 at
    /// its sides, and a check box that keeps its corners of 4. Then the color
    /// of the picked section's words and of a segmented switch's chosen
    /// segment.</summary>
    public class ControlMetricsTests
    {
        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static XDocument Doc(params string[] parts) => XDocument.Parse(ExtrasPageTests.RepoText(parts));

        /// <summary>The style a scope gives every control of a type.</summary>
        private static XElement Implicit(XElement scope, string type) => Assert.Single(scope.Elements(),
            e => e.Name.LocalName == "Style" && (string)e.Attribute("TargetType") == type && e.Attribute(Xaml + "Key") == null);

        private static string Setter(XElement style, string property) => (string)Assert.Single(style.Elements(),
            e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == property).Attribute("Value");

        private static XElement AppResources()
            => Doc("PadForge.App", "App.xaml").Root!.Elements().Single(e => e.Name.LocalName == "Application.Resources").Elements().Single();

        /// <summary>The app's own resources set the metrics. The corners are
        /// an entry of the application's own, outside the merged
        /// dictionaries, so they win over the 4 wpf-ui's controls read from
        /// the same key. A picker's words stand in the middle of its box:
        /// wpf-ui sets them at the top, 2 to 4 over the middle in every
        /// picker (seen in the page's pictures, 2026-11-12).</summary>
        [Fact]
        public void TheAppsControlsTakeTheBoardsMetrics()
        {
            var root = AppResources();
            var corners = Assert.Single(root.Elements(),
                e => e.Name.LocalName == "CornerRadius" && (string)e.Attribute(Xaml + "Key") == "ControlCornerRadius");
            Assert.Equal("6", corners.Value);

            var picker = Implicit(root, "ComboBox");
            Assert.Equal("{StaticResource DefaultComboBoxStyle}", (string)picker.Attribute("BasedOn"));
            Assert.Equal("34", Setter(picker, "MinHeight"));
            Assert.Equal("Center", Setter(picker, "VerticalContentAlignment"));
            // The part a click opens the list through is as tall as the
            // content, so the words keep 6 above and below them.
            Assert.Equal("8,6", Setter(picker, "Padding"));

            var button = Implicit(root, "Button");
            Assert.Equal("36", Setter(button, "MinHeight"));
            Assert.Equal("14,0", Setter(button, "Padding"));

            var check = Implicit(root, "CheckBox");
            Assert.Equal("4", Setter(check, "Border.CornerRadius"));
        }

        /// <summary>A page that gives a type a style of its own in place of
        /// the app's says that its setters mirror the app's, and they do.
        /// Left as they were, those pages kept the old 30 and 32.</summary>
        [Theory]
        [InlineData("DevicesPage.xaml")]
        [InlineData("ExtrasPage.xaml")]
        [InlineData("ProfilesPage.xaml")]
        [InlineData("SettingsPage.xaml")]
        public void APagesOwnStylesMirrorTheApps(string file)
        {
            var app = AppResources();
            var resources = Doc("PadForge.App", "Views", file).Root!.Elements()
                .Single(e => e.Name.LocalName == "UserControl.Resources");
            var mirrored = new[]
            {
                ("Button", new[] { "MinHeight", "Padding" }),
                ("ComboBox", new[] { "MinHeight", "Padding", "VerticalContentAlignment" }),
                ("CheckBox", new[] { "Border.CornerRadius" }),
            };
            int found = 0;
            foreach (var (type, properties) in mirrored)
            {
                var own = resources.Elements().SingleOrDefault(e => e.Name.LocalName == "Style"
                    && (string)e.Attribute("TargetType") == type && e.Attribute(Xaml + "Key") == null);
                if (own == null) continue;
                found++;
                foreach (string property in properties)
                    Assert.Equal(Setter(Implicit(app, type), property), Setter(own, property));
            }
            Assert.True(found >= 2, file + " gives fewer of the types a style of its own than it did");
        }

        /// <summary>A byte setting read in percent, as the Rumble board
        /// reads an adaptive trigger's range and strength (#574): every
        /// whole percent comes back as itself, the setting keeps its 0 to
        /// 255, and text that is no number leaves it as it was.</summary>
        [Fact]
        public void ABytesPercentComesBackAsItself()
        {
            var convert = new PadForge.Converters.BytePercentConverter();
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            for (int percent = 0; percent <= 100; percent++)
            {
                byte stored = (byte)convert.ConvertBack(percent.ToString(culture), typeof(byte), null, culture);
                Assert.Equal(percent.ToString(culture), convert.Convert(stored, typeof(string), null, culture));
                Assert.Equal((double)percent, convert.Convert(stored, typeof(double), null, culture));
            }
            Assert.Equal((byte)51, convert.ConvertBack(20.0, typeof(byte), null, culture));
            Assert.Equal((byte)255, convert.ConvertBack("140", typeof(byte), null, culture));
            Assert.Equal((byte)0, convert.ConvertBack(-3.0, typeof(byte), null, culture));
            Assert.Equal("56", convert.Convert((byte)0x90, typeof(string), null, culture));
            Assert.Same(System.Windows.Data.Binding.DoNothing, convert.ConvertBack("half", typeof(byte), null, culture));
        }

        /// <summary>The picked section in a controller's header and a
        /// segmented switch's chosen segment have their words in white on
        /// dark, as the boards draw them, a step over the text color. Light
        /// has no step over its text color and keeps it.</summary>
        [Fact]
        public void ThePickedSectionAndTheChosenSegmentTakeTheSelectedTextColor()
        {
            var brush = Assert.Single(AppResources().Descendants(),
                e => e.Name.LocalName == "SolidColorBrush" && (string)e.Attribute(Xaml + "Key") == "SelectedTextBrush");
            Assert.Equal("#FFFFFF", (string)brush.Attribute("Color"));
            Assert.Contains("SetBrush(\"SelectedTextBrush\", dark ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1A, 0x24, 0x33));",
                ExtrasPageTests.RepoText("PadForge.App", "Common", "EmberTheme.cs"));

            static string CheckedForeground(XDocument doc, string key)
            {
                var style = Assert.Single(doc.Descendants(), e => e.Name.LocalName == "Style" && (string)e.Attribute(Xaml + "Key") == key);
                var picked = Assert.Single(style.Descendants(), e => e.Name.LocalName == "Trigger"
                    && (string)e.Attribute("Property") == "IsChecked" && (string)e.Attribute("Value") == "True");
                return Setter(picked, "Foreground");
            }
            Assert.Equal("{DynamicResource SelectedTextBrush}", CheckedForeground(Doc("PadForge.App", "Views", "PadPage.xaml"), "SectionStripButton"));
            Assert.Equal("{DynamicResource SelectedTextBrush}", CheckedForeground(Doc("PadForge.App", "Resources", "ControllerIcons.xaml"), "SegmentRadio"));
        }
    }
}
