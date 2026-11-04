using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The ThingM blink(1) mk2 against OpenRGB's BlinkController
    /// (#557).</summary>
    public class ThingMBlinkLightingTests
    {
        [Fact]
        public void EachLight_IsAFadeCommand_InAFeatureReportWithId1()
        {
            var model = DeskLightKit.ModelOf(ThingMBlinkLighting.Instance, "ThingMController");
            Assert.Equal(2, model.Map.Lights.Count);
            var (io, session) = DeskLightKit.Painted(model, new[] { 0x112233, 0x445566 });
            Assert.True(session.Feature);
            // SendUpdate (BlinkController.cpp:64-85): 01 63, the color, no
            // fade time, the light from 1.
            Assert.Equal(new[] { "01 63 11 22 33 00 00 01 00", "01 63 44 55 66 00 00 02 00" },
                Calls(io).Select(c => Hex(c.Bytes)));
            DeskLightKit.HandsBackNothing(io, session);
        }
    }
}
