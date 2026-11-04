using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The BlinkyTape against OpenRGB's BlinkyTapeController (#557).</summary>
    public class BlinkyTapeLightingTests
    {
        [Fact]
        public void APicture_IsEveryLedsColorHeldTo254_AndTheEndByte()
        {
            // SetLEDs (BlinkyTapeController.cpp:64-105) for 512 LEDs.
            var frame = BlinkyTapeLighting.Frame(0xFFFF80);
            Assert.Equal(512 * 3 + 1, frame.Length);
            Assert.Equal("FE FE 80 FE FE 80", Hex(frame, 6));
            Assert.Equal("FE FE 80 FF", Hex(frame.Skip(frame.Length - 4).ToArray()));
            Assert.Equal(1, frame.Count(b => b == 0xFF));
        }

        [Fact]
        public void ASession_WritesThePicture_AndHasNothingToHandBack()
        {
            var port = new FakeSerialPort();
            var family = new BlinkyTapeLighting(() => Array.Empty<ComPort>(), _ => Guid.Empty, _ => port);
            var model = family.Model;
            Assert.Equal(LightKind.Zone, Assert.Single(model.Map.Lights).Kind);
            Assert.Empty(model.Programs);
            var session = model.BeginOwn(model.Open(@"\\.\COM3"), _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, model.Map.Lights, new[] { 0x102030 }));
            Assert.Equal(Hex(BlinkyTapeLighting.Frame(0x102030)), Hex(Assert.Single(port.Written())));
            Assert.Empty(session.CrashFrames);
            Assert.True(session.HandBack(null, _ => { }));
            Assert.Single(port.Written());
            Assert.False(model.WriteOnce(@"\\.\COM3", new byte[] { 1 }, 100));
        }
    }
}
