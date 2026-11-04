using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Dream Cheeky Webmail Notifier against OpenRGB's
    /// DreamCheekyController (#557).</summary>
    public class DreamCheekyLightingTests
    {
        [Fact]
        public void TheFirstPicture_FollowsTheFourOpeningReports_AndLaterOnesComeAlone()
        {
            var model = DeskLightKit.ModelOf(DreamCheekyLighting.Instance, "DreamCheekyController");
            var (io, session) = DeskLightKit.Painted(model, new[] { 0xFF8003 });
            var calls = Calls(io);
            // DreamCheekyController.cpp:26-34, then SetColor (lines 65-89):
            // 255 scales to 64, 0x80 to 0x20, 3 to 0.
            Assert.Equal(new[]
            {
                "00 1F 02 00 5F 00 00 1F 03",
                "00 00 02 00 5F 00 00 1F 04",
                "00 00 00 00 00 00 00 1F 05",
                "00 00 00 00 00 00 00 00 01",
                "00 40 20 00 00 00 00 1F 05",
            }, calls.Select(c => Hex(c.Bytes)));
            Assert.All(calls, c => Assert.Equal("out", c.Kind));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x04FE01 }));
            Assert.Equal("00 01 3F 00 00 00 00 1F 05", Hex(Calls(io)[5].Bytes));
            Assert.Equal(6, io.Calls.Count);
            DeskLightKit.HandsBackNothing(io, session);
        }
    }
}
