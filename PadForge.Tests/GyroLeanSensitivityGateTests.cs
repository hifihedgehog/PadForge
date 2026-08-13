using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The #292 sensitivity-dial defect, pinned at the two view-model
    /// predicates that were the only sites not sorting the gravity-lean
    /// pair before the generic "Gyro " family. A lean row must gate the
    /// dial bound to the generic Sensitivity field (the field ReadGyroLean
    /// scales by), never the GyroSensitivity dial (rate-only).
    /// </summary>
    public class GyroLeanSensitivityGateTests
    {
        private static MappingItem NewRow(string desc)
            => new MappingItem("Stick X", "LeftThumbAxisX", MappingCategory.LeftStick)
               { SourceDescriptor = desc };

        [Theory]
        [InlineData("Gyro Pitch")]
        [InlineData("Gyro Yaw")]
        [InlineData("Gyro Roll")]
        [InlineData("Gyro Horizontal")]
        [InlineData("Gyro L Pitch")]
        [InlineData("Gyro R Yaw")]
        public void RateDescriptors_GateTheGyroDial_NotTheLeanDial(string desc)
        {
            var extra = new MappingSourceItem { Descriptor = desc };
            Assert.True(extra.IsGyroSource);
            Assert.False(extra.IsGyroLeanSource);

            var row = NewRow(desc);
            Assert.True(row.IsGyroSource);
            Assert.False(row.IsGyroLeanSource);
        }

        [Theory]
        [InlineData("Gyro Lean X")]
        [InlineData("Gyro Lean Y")]
        public void LeanDescriptors_GateTheLeanDial_NotTheGyroDial(string desc)
        {
            var extra = new MappingSourceItem { Descriptor = desc };
            Assert.False(extra.IsGyroSource);
            Assert.True(extra.IsGyroLeanSource);

            var row = NewRow(desc);
            Assert.False(row.IsGyroSource);
            Assert.True(row.IsGyroLeanSource);
        }

        [Fact]
        public void LegacyPrefixedLeanPrimary_StillGatesTheLeanDial()
        {
            // The legacy row grammar stores Invert as a leading "I"; the
            // MappingItem predicates strip it before classifying.
            var row = NewRow("IGyro Lean X");
            Assert.False(row.IsGyroSource);
            Assert.True(row.IsGyroLeanSource);
        }

        [Fact]
        public void NonGyroDescriptors_GateNeitherDial()
        {
            var extra = new MappingSourceItem { Descriptor = "Axis 2" };
            Assert.False(extra.IsGyroSource);
            Assert.False(extra.IsGyroLeanSource);
        }
    }
}
