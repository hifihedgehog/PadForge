using System.IO;
using System.Xml.Serialization;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Same persistence surface as GyroEngageStickSideTests, for the #120
    /// per-direction selector (Additional Context wishlist): round-trip,
    /// legacy-default back-compat to "Full", and checksum sensitivity. Guards
    /// the compile-clean "reverts on restart" failure class for the new field.
    /// </summary>
    public class GyroEngageStickDirectionTests
    {
        private static PadSetting RoundTrip(PadSetting ps)
        {
            var ser = new XmlSerializer(typeof(PadSetting));
            using var sw = new StringWriter();
            ser.Serialize(sw, ps);
            using var sr = new StringReader(sw.ToString());
            return (PadSetting)ser.Deserialize(sr);
        }

        [Theory]
        [InlineData("Full")]
        [InlineData("X")]
        [InlineData("Y")]
        [InlineData("XNeg")]
        [InlineData("XPos")]
        [InlineData("YNeg")]
        [InlineData("YPos")]
        public void GyroEngageStickDirection_SurvivesXmlRoundTrip(string direction)
        {
            var ps = new PadSetting { GyroEngageStickDirection = direction };
            Assert.Equal(direction, RoundTrip(ps).GyroEngageStickDirection);
        }

        [Fact]
        public void GyroEngageStickDirection_DefaultsToFull_OnFreshAndLegacyXml()
        {
            // A fresh PadSetting defaults to "Full" (back-compat: the radial
            // max(|x|,|y|) gate the core shipped with).
            Assert.Equal("Full", new PadSetting().GyroEngageStickDirection);

            // A legacy settings blob with NO <GyroEngageStickDirection> element
            // must deserialize to "Full", not empty, so pre-selector profiles
            // keep their original radial Easy Aim behavior.
            const string legacy = "<?xml version=\"1.0\"?><PadSetting xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"></PadSetting>";
            var ser = new XmlSerializer(typeof(PadSetting));
            using var sr = new StringReader(legacy);
            var ps = (PadSetting)ser.Deserialize(sr);
            Assert.Equal("Full", ps.GyroEngageStickDirection);
        }

        [Fact]
        public void GyroEngageStickDirection_ChangesTheContentChecksum()
        {
            // Two settings identical except the direction must hash differently,
            // or profile-change detection would miss a direction switch.
            var full = new PadSetting { GyroEngageStickDirection = "Full" };
            var xpos = new PadSetting { GyroEngageStickDirection = "XPos" };
            var yneg = new PadSetting { GyroEngageStickDirection = "YNeg" };

            Assert.NotEqual(full.ComputeChecksum(), xpos.ComputeChecksum());
            Assert.NotEqual(full.ComputeChecksum(), yneg.ComputeChecksum());
            Assert.NotEqual(xpos.ComputeChecksum(), yneg.ComputeChecksum());
        }
    }
}
