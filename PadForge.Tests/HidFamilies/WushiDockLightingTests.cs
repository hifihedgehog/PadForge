using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The JSAUX RGB Docking Station against OpenRGB's
    /// WushiL50USBController (#557).</summary>
    public class WushiDockLightingTests
    {
        [Fact]
        public void APicture_IsTheDirectModesOneFeatureReport()
        {
            var model = DeskLightKit.ModelOf(WushiDockLighting.Instance, "WushiController");
            Assert.Equal(new[] { 1, 2, 3, 4 }, model.Map.Lights.Select(l => l.Zone));
            var (io, session) = DeskLightKit.Painted(model, new[] { 0x112233, 0x445566, 0x778899, 0xAABBCC });
            Assert.True(session.Feature);
            var call = Assert.Single(Calls(io));
            Assert.Equal("set", call.Kind);
            Assert.Equal(65, call.Bytes.Length);
            // setMode on Windows (WushiL50USBController.cpp:50-88): CC, 16,
            // effect 1, speed 1, brightness 2, the zones, a zero, the
            // directions 0 0.
            Assert.Equal("CC 16 01 01 02 11 22 33 44 55 66 77 88 99 AA BB CC 00 00 00", Hex(call.Bytes, 20));
            Assert.All(call.Bytes.Skip(17), b => Assert.Equal(0, b));
            DeskLightKit.HandsBackNothing(io, session);
        }
    }
}
