using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Mad Catz Cyborg Gaming Light against OpenRGB's
    /// MadCatzCyborgController (#557).</summary>
    public class MadCatzCyborgLightingTests
    {
        [Fact]
        public void TheFirstPicture_FollowsEnableAndIntensity_AsFeatureReports()
        {
            var model = DeskLightKit.ModelOf(MadCatzCyborgLighting.Instance, "MadCatzCyborgController");
            var (io, session) = DeskLightKit.Painted(model, new[] { 0x112233 });
            Assert.True(session.Feature);
            var calls = Calls(io);
            // Initialize, SetIntensity(100) and SetLEDColor
            // (MadCatzCyborgController.cpp:47-99).
            Assert.Equal(new[] { "A1 00", "A6 00 64", "A2 00 11 22 33 00 00 00 00" }, calls.Select(c => Hex(c.Bytes)));
            Assert.All(calls, c => Assert.Equal("set", c.Kind));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x445566 }));
            Assert.Equal("A2 00 44 55 66 00 00 00 00", Hex(Calls(io)[3].Bytes));
            Assert.Equal(4, io.Calls.Count);
            DeskLightKit.HandsBackNothing(io, session);
        }
    }
}
