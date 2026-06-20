using System.IO;
using System.Xml.Serialization;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Closes the cheapest cite-verify residual for issue #120 without
    /// hardware: the serialization / clone / default-collapse surface where
    /// compile-clean gyro-field "reverts on restart" bugs have shipped before.
    /// Covers persistence round-trip, the legacy-default back-compat, and the
    /// content checksum's sensitivity to the side selector.
    /// </summary>
    public class GyroEngageStickSideTests
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
        [InlineData("Left")]
        [InlineData("Either")]
        [InlineData("Right")]
        public void GyroEngageStickSide_SurvivesXmlRoundTrip(string side)
        {
            var ps = new PadSetting { GyroEngageStickSide = side };
            Assert.Equal(side, RoundTrip(ps).GyroEngageStickSide);
        }

        [Fact]
        public void GyroEngageStickSide_DefaultsToRight_OnFreshAndLegacyXml()
        {
            // A fresh PadSetting defaults to "Right" (back-compat).
            Assert.Equal("Right", new PadSetting().GyroEngageStickSide);

            // A legacy settings blob with NO <GyroEngageStickSide> element must
            // deserialize to "Right", not empty, so pre-selector profiles keep
            // their original right-stick Easy Aim behavior.
            const string legacy = "<?xml version=\"1.0\"?><PadSetting xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"></PadSetting>";
            var ser = new XmlSerializer(typeof(PadSetting));
            using var sr = new StringReader(legacy);
            var ps = (PadSetting)ser.Deserialize(sr);
            Assert.Equal("Right", ps.GyroEngageStickSide);
        }

        [Fact]
        public void GyroEngageStickSide_ChangesTheContentChecksum()
        {
            // Two settings identical except the side must hash differently, or
            // profile-change detection would miss a side switch.
            var right = new PadSetting { GyroEngageStickSide = "Right" };
            var left = new PadSetting { GyroEngageStickSide = "Left" };
            var either = new PadSetting { GyroEngageStickSide = "Either" };

            Assert.NotEqual(right.ComputeChecksum(), left.ComputeChecksum());
            Assert.NotEqual(right.ComputeChecksum(), either.ComputeChecksum());
            Assert.NotEqual(left.ComputeChecksum(), either.ComputeChecksum());
        }
    }
}
