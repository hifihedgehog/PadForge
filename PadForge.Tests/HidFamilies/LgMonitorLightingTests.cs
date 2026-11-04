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
        /// 64 data bytes, C1 02 91 00, the colors, the checksum, ED. A channel
        /// at zero goes out as 1 (fixColor, LG_UltraGear_27GN950.js:137 and
        /// LG_UltraGear_38GL950G.js:96).</summary>
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
            var first = Ones();
            MonitorTest.Bytes("00 53 43 C1 02 91 00 01 02 03").CopyTo(first, 0);
            Assert.Equal(MonitorTest.Hex(first), MonitorTest.Hex(sent[2]));
            Assert.Equal(MonitorTest.Hex(Ones()), MonitorTest.Hex(sent[3]));
            // Data bytes 147 to 152: the last color, then the checksum. The 46
            // colors at 01 01 01 XOR to nothing, leaving 42 XOR 01 02 03 01 01 FF.
            var third = Ones();
            MonitorTest.Bytes("01 01 FF BD 45 44").CopyTo(third, 20);
            for (int i = 26; i < third.Length; i++) third[i] = 0;
            Assert.Equal(MonitorTest.Hex(third), MonitorTest.Hex(sent[4]));

            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new int[48]));
            sent = io.Sent("O");
            Assert.Equal(3, sent.Count);
            // All black, 48 colors at 01 01 01: 53 43 C1 02 91 00 XOR to 42.
            Assert.Equal("42 45 44", MonitorTest.Hex(sent[2], 23, 3));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Failed,
                Model().Begin(io, io.Wait).Paint(new MonitorChannel { FailAfter = 1 }, _ => { }, lights, colors));

            static byte[] Ones()
            {
                var report = new byte[65];
                Array.Fill(report, (byte)1, 1, 64);
                return report;
            }
        }

        /// <summary>The 38GL950G's picture, which only SignalRGB's plugin
        /// sends, takes the same rule.</summary>
        [Fact]
        public void NoChannelGoesOutAtZero_OnEitherMonitor()
        {
            Assert.Equal(1, LgMonitorLighting.Channel(0));
            Assert.Equal(1, LgMonitorLighting.Channel(0xFF00));
            Assert.Equal(0x80, LgMonitorLighting.Channel(0x80));
            var known = MonitorTest.CollectionOf(Assert.Single(OpenRgbRegistrations.Of(LgMonitorLighting.OpenRgbFamily)));
            var other = known with
            {
                ProductId = LgMonitorLighting.Ultragear38,
                InterfaceNumber = 0,
                Path = known.Path.Replace("pid_9a8a&mi_01", "pid_9a57&mi_00"),
                UsagePage = 0xFF00,
            };
            foreach (var collection in new[] { known, other })
            {
                var io = new MonitorChannel();
                var session = LgMonitorLighting.Instance.Match(collection).Begin(io, io.Wait);
                Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, LgMonitorLighting.Lights(), new int[48]));
                var sent = io.Sent("O");
                Assert.Equal(5, sent.Count);
                // The first color, red, green and blue, after the six bytes of the command.
                Assert.Equal("01 01 01", MonitorTest.Hex(sent[2], 7, 3));
                Assert.DoesNotContain((byte)0, sent[3].Skip(1));
            }
        }
    }
}
