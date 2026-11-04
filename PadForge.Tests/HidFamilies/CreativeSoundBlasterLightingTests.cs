using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Sound BlasterX G6 against OpenRGB's
    /// CreativeSoundBlasterXG6Controller (#557).</summary>
    public class CreativeSoundBlasterLightingTests
    {
        [Fact]
        public void APicture_IsSetLedColorsThreeReports()
        {
            var model = DeskLightKit.ModelOf(CreativeSoundBlasterLighting.Instance, "CreativeController");
            var light = Assert.Single(model.Map.Lights);
            Assert.Equal(LightKind.Logo, light.Kind);
            var (io, session) = DeskLightKit.Painted(model, new[] { 0x112233 });
            Assert.False(session.Feature);
            var calls = Calls(io);
            Assert.Equal(new[] { "out", "out", "out" }, calls.Select(c => c.Kind));
            Assert.All(calls, c => Assert.Equal(64, c.Bytes.Length));
            // CreativeSoundBlasterXG6Controller.cpp:46-99: report ID 0, then
            // the three commands, the last with brightness FF and blue,
            // green, red.
            Assert.Equal("00 5A 3A 02 06 01 00", Hex(calls[0].Bytes, 7));
            Assert.Equal("00 5A 3A 06 04 00 03 01 00 01 00", Hex(calls[1].Bytes, 11));
            Assert.Equal("00 5A 3A 09 0A 00 03 01 01 FF 33 22 11 00", Hex(calls[2].Bytes, 14));
            Assert.All(calls, c => Assert.All(c.Bytes.Skip(13), b => Assert.Equal(0, b)));
            DeskLightKit.HandsBackNothing(io, session);
        }
    }
}
