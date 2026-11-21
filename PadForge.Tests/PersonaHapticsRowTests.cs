using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The Play DualSense Haptics on This Controller switch and its
    /// gain show on every pad that plays haptic tones, a Flydigi Apex 6 Pro
    /// included. #586 hid the high-tone filter on a pad that plays samples,
    /// since it has no pitch to filter, and the switch sat in the same
    /// panel, so an Apex 6 Pro could never turn on the persona haptic track
    /// its grips play (HapticToneService's grip lane follows
    /// DeviceConfig.AudioPersonaHapticsEnabled, off by default).</summary>
    public class PersonaHapticsRowTests
    {
        private static XDocument Page() => XDocument.Load(AuditDelta20260823Tests.FindRepoFile(
            Path.Combine("PadForge.App", "Views", "PadPage.xaml")));

        private static string[] Gates(XElement element) => element.Ancestors()
            .Select(a => (string)a.Attribute("Visibility") ?? string.Empty)
            .Where(v => v.Length > 0)
            .ToArray();

        [Fact]
        public void ThePersonaSwitchAndItsGain_ShowWhereverTheDevicePlaysHapticTones()
        {
            var page = Page();
            var toggle = page.Descendants().Single(e => e.Name.LocalName == "CheckBox"
                && ((string)e.Attribute("IsChecked") ?? string.Empty).Contains("DeviceConfig.AudioPersonaHapticsEnabled"));
            var gain = page.Descendants().Single(e => e.Name.LocalName == "Slider"
                && ((string)e.Attribute("Value") ?? string.Empty).Contains("DeviceConfig.AudioPersonaHapticsGain"));
            foreach (var element in new[] { toggle, gain })
            {
                var gates = Gates(element);
                Assert.DoesNotContain(gates, g => g.Contains("SelectedDeviceHasToneFilter"));
                Assert.Contains(gates, g => g.Contains("SelectedDeviceHasHapticTones"));
            }
        }

        [Fact]
        public void TheToneFilter_StillHidesOnAPadThatPlaysSamples()
        {
            var filter = Page().Descendants().Single(e => e.Name.LocalName == "ComboBox"
                && ((string)e.Attribute("SelectedValue") ?? string.Empty).Contains("DeviceConfig.AudioToneFilterMode"));
            Assert.Contains(Gates(filter), g => g.Contains("SelectedDeviceHasToneFilter"));
        }
    }
}
