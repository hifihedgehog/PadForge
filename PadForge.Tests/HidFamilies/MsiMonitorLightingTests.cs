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
    /// <summary>MSI's gaming-controller monitors against OpenRGB's
    /// MSIMonitorController (#557).</summary>
    public class MsiMonitorLightingTests
    {
        private static HidLightingModel Model()
            => MsiMonitorLighting.Instance.Match(MonitorTest.CollectionOf(
                Assert.Single(OpenRgbRegistrations.Of(MsiMonitorLighting.OpenRgbFamily))));

        /// <summary>msi_monitor_72_layouts (MSIMonitorControllerDetect.cpp:54-82),
        /// written out row by row.</summary>
        [Fact]
        public void TheLayoutTable_IsOpenRgbs()
        {
            var expected = new (int Version, int Leds, int Direction)[]
            {
                (34, 9, 1), (5, 12, 0), (8, 24, 3), (10, 9, 1), (12, 9, 1), (14, 14, 1), (17, 8, 0), (20, 12, 1),
                (21, 18, 1), (22, 24, 1), (24, 12, 1), (25, 24, 1), (28, 9, 1), (29, 24, 0), (30, 21, 0), (32, 7, 1),
                (33, 7, 1), (36, 9, 1), (37, 22, 1), (38, 20, 1), (39, 8, 0), (40, 12, 1), (41, 14, 0), (42, 22, 4),
                (43, 21, 1), (44, 9, 1), (46, 9, 1),
            };
            Assert.Equal(expected, MsiMonitorLighting.Layouts.Select(l => ((int)l.Version, l.Leds, l.Direction)));
            // The versions OpenRGB leaves out light as the 0x71 layout.
            foreach (int version in new[] { 0, 1, 2, 3, 4, 6, 7, 11, 13, 15, 31, 47 })
                Assert.Null(MsiMonitorLighting.LayoutOf(version));
        }

        [Fact]
        public void ACollectionWithShortFeatureReports_IsNotListed()
        {
            var r = Assert.Single(OpenRgbRegistrations.Of(MsiMonitorLighting.OpenRgbFamily));
            Assert.NotNull(MsiMonitorLighting.Instance.Match(MonitorTest.CollectionOf(r, feature: 78)));
            Assert.Null(MsiMonitorLighting.Instance.Match(MonitorTest.CollectionOf(r, feature: 77)));
            Assert.Equal(9, Model().Map.Lights.Count);
        }

        /// <summary>GetLayoutVersion (MSIMonitorController.cpp:145-188):
        /// queued reports drained with no wait, 01 B0, an answer within 500
        /// ms, 01 5A and the version at byte 3.</summary>
        [Fact]
        public void ASession_AsksTheVersion_AndTakesItsLayout()
        {
            var io = new MonitorChannel();
            // A stale answer waits and is drained. The answer to the query
            // comes after it.
            io.Inputs.Enqueue(MonitorTest.Bytes("01 5A 00 22"));
            io.Answers.Enqueue(MonitorTest.Bytes("01 5A 00 05"));
            var session = (MsiMonitorLighting.Session)Model().Begin(io, io.Wait);
            Assert.Equal("I0 I0 O I500", io.Kinds());
            Assert.Equal(MonitorTest.Report("01 B0", 64), MonitorTest.Hex(io.Sent("O")[0]));
            Assert.Equal(5, session.Layout.Value.Version);
            Assert.Equal(12, session.Map.Lights.Count);
            Assert.EndsWith(":72v5", session.Map.Product);

            // No answer keeps the 0x71 layout.
            io = new MonitorChannel();
            session = (MsiMonitorLighting.Session)Model().Begin(io, io.Wait);
            Assert.Equal("I0 O I500", io.Kinds());
            Assert.Null(session.Layout);
            Assert.Equal(9, session.Map.Lights.Count);
            Assert.EndsWith(":71", session.Map.Product);
        }

        /// <summary>An answer that is not 01 5A, a version not in the table,
        /// or a query that did not go out, is version 0, the 0x71
        /// layout.</summary>
        [Fact]
        public void AnAnswerThatIsNotAnAck_KeepsThe71Layout()
        {
            Assert.Equal(0, Asked("01 5B 00 22"));
            Assert.Equal(34, Asked("01 5A 00 22"));
            Assert.Equal(7, Asked("01 5A 00 07"));
            Assert.Null(MsiMonitorLighting.LayoutOf(7));
            var failing = new MonitorChannel { FailAfter = 0 };
            Assert.Equal(0, MsiMonitorLighting.AskVersion(failing));
            Assert.Equal("I0", failing.Kinds());
        }

        private static int Asked(string answer)
        {
            var io = new MonitorChannel();
            io.Answers.Enqueue(MonitorTest.Bytes(answer));
            return MsiMonitorLighting.AskVersion(io);
        }

        /// <summary>Set (MSIMonitorController.cpp:73-143) for Direct: mode 01,
        /// FF 00 00 nine times from 23, the colors from 50, last byte 00, then
        /// 5 ms.</summary>
        [Fact]
        public void A71Picture_IsSet()
        {
            var colors = new int[9];
            colors[0] = 0x112233;
            colors[8] = 0xAABBCC;
            var expected = new byte[78];
            MonitorTest.Bytes("71 01 00 00 00 01 64 00 00 00 00 00 01 00 00 00 01 64 00 00 00 00 00").CopyTo(expected, 0);
            for (int i = 0; i < 9; i++) expected[23 + 3 * i] = 0xFF;
            MonitorTest.Bytes("11 22 33").CopyTo(expected, 50);
            MonitorTest.Bytes("AA BB CC").CopyTo(expected, 74);
            Assert.Equal(MonitorTest.Hex(expected), MonitorTest.Hex(MsiMonitorLighting.Frame71(colors)));

            var session = new MsiMonitorLighting.Session(null, MsiMonitorLighting.MapOf(0x3FA4, null));
            var io = new MonitorChannel();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, colors));
            Assert.Equal("F W5", io.Kinds());
            Assert.Equal(MonitorTest.Hex(expected), MonitorTest.Hex(io.Sent("F")[0]));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new MonitorChannel { FailAfter = 0 }, _ => { }, session.Map.Lights, colors));
        }

        /// <summary>SetMode72 for Direct (MSIMonitorController.cpp:190-263 and
        /// RGBController_MSIMonitor72.cpp:508-565): two blocks of 01, the
        /// first color, speed 0, brightness 100, black, 80, the colors at 95
        /// mirrored for a right-to-left fill, store byte 00.</summary>
        [Fact]
        public void A72Picture_IsSetMode72_MirroredRightToLeft()
        {
            var colors = Enumerable.Range(1, 12).Select(i => 0x010101 * i).ToArray();
            var expected = new byte[168];
            expected[0] = 0x72;
            foreach (int b in new[] { 1, 12 })
                MonitorTest.Bytes("01 01 01 01 00 64 00 00 00 80 00").CopyTo(expected, b);
            for (int slot = 0; slot < 12; slot++)
            {
                byte v = (byte)(12 - slot);
                expected[95 + 3 * slot] = v;
                expected[96 + 3 * slot] = v;
                expected[97 + 3 * slot] = v;
            }
            var layout = MsiMonitorLighting.LayoutOf(5).Value;
            Assert.Equal(MsiMonitorLighting.RightToLeft, layout.Direction);
            Assert.Equal(MonitorTest.Hex(expected), MonitorTest.Hex(MsiMonitorLighting.Frame72(colors, layout.Leds, layout.Direction)));

            // Left to right keeps the order, and the first color is the
            // first LED's either way.
            var ltr = MsiMonitorLighting.Frame72(colors, 12, MsiMonitorLighting.LeftToRight);
            Assert.Equal("01 01 01 02 02 02", MonitorTest.Hex(ltr, 95, 6));
            Assert.Equal("01 01 01 01", MonitorTest.Hex(ltr, 1, 4));
            Assert.Equal(0, ltr[167]);
            Assert.All(ltr.Skip(23).Take(72), b => Assert.Equal(0, b));

            var session = new MsiMonitorLighting.Session(layout, MsiMonitorLighting.MapOf(0x3FA4, layout));
            var io = new MonitorChannel();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, session.Map.Lights, colors));
            Assert.Equal("F W5", io.Kinds());
            Assert.Equal(MonitorTest.Hex(expected), MonitorTest.Hex(io.Sent("F")[0]));
        }
    }
}
