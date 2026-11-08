using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The dials bound to a row's generic Sensitivity (the Gyro
    /// Lean rows, primary and extra source) reach the same values as the
    /// row's own item holds, and their text boxes show them: a slider whose
    /// Minimum sat above the item's floor showed a value it could not reach,
    /// and a two-place box showed 0.00 for a rotation's 0.001.</summary>
    public class RowSensitivitySliderTests
    {
        [Fact]
        public void EveryRowSensitivityDial_ReachesTheItemsRange_AndShowsIt()
        {
            double floor = new MappingSourceItem { Sensitivity = 0 }.Sensitivity;
            double ceiling = new MappingSourceItem { Sensitivity = 100 }.Sensitivity;
            Assert.Equal(floor, new MappingItem("A", "LeftThumbAxisX", MappingCategory.LeftStick) { Sensitivity = 0 }.Sensitivity);
            Assert.Equal(ceiling, new MappingItem("A", "LeftThumbAxisX", MappingCategory.LeftStick) { Sensitivity = 100 }.Sensitivity);

            string path = AuditDelta20260823Tests.FindRepoFile(Path.Combine("PadForge.App", "Views", "PadPage.xaml"));
            var sliders = XDocument.Load(path).Descendants()
                .Where(e => e.Name.LocalName == "Slider"
                    && ((string)e.Attribute("Value") ?? "").StartsWith("{Binding Sensitivity,", StringComparison.Ordinal)
                    && ((string)e.Parent?.Attribute("Visibility") ?? "").Contains("IsGyroLeanSource", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, sliders.Count);
            foreach (var slider in sliders)
            {
                Assert.Equal(floor, double.Parse((string)slider.Attribute("Minimum"), CultureInfo.InvariantCulture));
                Assert.Equal(ceiling, double.Parse((string)slider.Attribute("Maximum"), CultureInfo.InvariantCulture));
                var box = slider.ElementsAfterSelf().First(e => e.Name.LocalName == "TextBox");
                string text = (string)box.Attribute("Text");
                Assert.StartsWith("{Binding Sensitivity,", text);
                int start = text.IndexOf("{0:", StringComparison.Ordinal);
                string format = text.Substring(start, text.IndexOf('}', start) - start + 1);
                string shown = string.Format(CultureInfo.InvariantCulture, format, floor);
                Assert.Equal(floor, double.Parse(shown, CultureInfo.InvariantCulture));
            }
        }
    }
}
