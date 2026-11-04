using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests.MonitorFamilies
{
    /// <summary>The LG 27GN950-B against OpenRGB's LGMonitorController
    /// (#557).</summary>
    public class LgMonitorLightingTests
    {
        private static HidLightingModel Model()
            => LgMonitorLighting.Instance.Match(MonitorTest.CollectionOf(
                Assert.Single(OpenRgbRegistrations.Of(LgMonitorLighting.OpenRgbFamily))));

        [Fact]
        public void TheModel_Has48EdgeLights_AndAKeepAliveOf500Ms()
        {
            var model = Model();
            Assert.Equal(48, model.Map.Lights.Count);
            Assert.All(model.Map.Lights, l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(500, model.KeepAliveMs);
            Assert.Equal(500, model.KeepAliveIdleMs);
            Assert.Equal(50, model.RepaintMs);
        }

        /// <summary>TurnOn and EnableMode(08) (LGMonitorController.cpp:157-213),
        /// as SignalRGB's plugin writes them too (turn_on and
        /// color_video_sync, LG_UltraGear_27GN950.js:22-31).</summary>
        [Fact]
        public void TheCommands_AreFramedWithTheirChecksums()
        {
            Assert.Equal(MonitorTest.Report("00 53 43 CF 02 02 01 00 DE 45 44", 65), MonitorTest.Hex(LgMonitorLighting.TurnOnFrame()));
            Assert.Equal(MonitorTest.Report("00 53 43 CA 02 02 03 08 D1 45 44", 65), MonitorTest.Hex(LgMonitorLighting.DirectModeFrame()));
        }

        /// <summary>SetDirect (lines 51-107): the first picture turns the
        /// lighting on and selects Direct, every picture is three reports of
        /// 64 data bytes, C1 02 91 00, the colors, the checksum, ED.</summary>
        [Fact]
        public void APicture_IsThreeReports_AfterOnAndDirectOnce()
        {
            var io = new MonitorChannel();
            var session = Model().Begin(io, io.Wait);
            var colors = new int[48];
            colors[0] = 0x010203;
            colors[47] = 0x0000FF;
            var lights = LgMonitorLighting.Lights();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, colors));
            var sent = io.Sent("O");
            Assert.Equal(5, sent.Count);
            Assert.Equal(MonitorTest.Hex(LgMonitorLighting.TurnOnFrame()), MonitorTest.Hex(sent[0]));
            Assert.Equal(MonitorTest.Hex(LgMonitorLighting.DirectModeFrame()), MonitorTest.Hex(sent[1]));
            Assert.Equal(MonitorTest.Report("00 53 43 C1 02 91 00 01 02 03", 65), MonitorTest.Hex(sent[2]));
            Assert.Equal(MonitorTest.Report("00", 65), MonitorTest.Hex(sent[3]));
            // Data bytes 147 to 152: the last color, 42 XOR 01 02 03 FF, ED.
            var third = new byte[65];
            MonitorTest.Bytes("00 00 FF BD 45 44").CopyTo(third, 20);
            Assert.Equal(MonitorTest.Hex(third), MonitorTest.Hex(sent[4]));

            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new int[48]));
            sent = io.Sent("O");
            Assert.Equal(3, sent.Count);
            // All black: 53 43 C1 02 91 00 XOR to 42.
            Assert.Equal("42 45 44", MonitorTest.Hex(sent[2], 23, 3));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Failed,
                Model().Begin(io, io.Wait).Paint(new MonitorChannel { FailAfter = 1 }, _ => { }, lights, colors));
        }
    }
}
