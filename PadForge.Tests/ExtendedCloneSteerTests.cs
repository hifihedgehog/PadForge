using System;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Extended slot's Clone for another controller (#600): a mouse
    /// primary another device owns moves aside as an extra source with its
    /// Steer input, for Mouse Position and Mouse Speed as for the travel
    /// family, and the row that takes the cloned input keeps no gate.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class ExtendedCloneSteerTests
    {
        [Fact]
        public void AMousePositionPrimary_TakesItsSteerInputAlong_AndLeavesTheRowNoGate()
        {
            string other = Guid.NewGuid().ToString();
            var row = new MappingItem("Axis 1", "Axis1", MappingCategory.LeftStick);
            row.LoadDescriptor("Mouse Position X");
            row.PrimarySourceDeviceGuid = other;
            row.ParamTravelSteer = MappingSource.TravelSteerHeldStay;
            row.GateDescriptor = "Button 2";
            row.Gate2Descriptor = "Button 3";
            Assert.True(row.IsMouseSteerSource);

            PadForge.Views.PadPage.ClearPrimaryForClone(row, primaryIsCloneDevice: false);

            Assert.Equal(string.Empty, row.GateDescriptor);
            Assert.Equal(string.Empty, row.Gate2Descriptor);
            var moved = Assert.Single(row.ExtraSources);
            Assert.Equal("Mouse Position X", moved.Descriptor);
            Assert.Equal("Button 2", moved.GateDescriptor);
            Assert.Equal("Button 3", moved.Gate2Descriptor);
            Assert.Equal(MappingSource.TravelSteerHeldStay, moved.ParamTravelSteer);
        }
    }
}
