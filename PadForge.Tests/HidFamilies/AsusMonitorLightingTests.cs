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
    /// <summary>The ASUS monitors against OpenRGB's AsusMonitorController
    /// (#557).</summary>
    public class AsusMonitorLightingTests
    {
        private static HidLightingModel Model()
            => AsusMonitorLighting.Instance.Match(MonitorTest.CollectionOf(
                OpenRgbRegistrations.Of(AsusMonitorLighting.OpenRgbFamily).First()));

        [Fact]
        public void TheLightBar_IsOnInterface2_AndTheMonitorsOnInterface1()
        {
            var registrations = OpenRgbRegistrations.Of(AsusMonitorLighting.OpenRgbFamily).ToList();
            Assert.All(registrations, r => Assert.Equal((0xFF72, 0xA1), (r.UsagePage, r.Usage)));
            Assert.Equal(2, registrations.Single(r => r.ProductId == 0x1AC8).Interface);
            Assert.All(registrations.Where(r => r.ProductId != 0x1AC8), r => Assert.Equal(1, r.Interface));
            var bar = registrations.Single(r => r.ProductId == 0x1AC8);
            Assert.Null(AsusMonitorLighting.Instance.Match(MonitorTest.CollectionOf(bar) with
            {
                InterfaceNumber = 1,
                Path = MonitorTest.CollectionOf(bar).Path.Replace("&mi_02", "&mi_01"),
            }));
        }

        /// <summary>GetNumberOfLEDs and SendInit (AsusMonitorController.cpp:51-81):
        /// EC B0, the answer's byte 32, then EC 35.</summary>
        [Fact]
        public void ASession_ReadsTheCount_ThenSendsInit()
        {
            var io = new MonitorChannel();
            var answer = new byte[65];
            answer[0] = 0xEC;
            answer[32] = 7;
            io.Answers.Enqueue(answer);
            var session = Model().Begin(io, io.Wait);
            Assert.NotNull(session);
            Assert.Equal("O I1000 O", io.Kinds());
            Assert.Equal(MonitorTest.Report("EC B0", 65), MonitorTest.Hex(io.Sent("O")[0]));
            Assert.Equal(MonitorTest.Report("EC 35 00 00 00 FF 00 00 01", 65), MonitorTest.Hex(io.Sent("O")[1]));
            Assert.Equal(7, session.Map.Lights.Count);
            Assert.All(session.Map.Lights, l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(Enumerable.Range(0, 7), session.Map.Lights.Select(l => l.Address));
            Assert.EndsWith(":7", session.Map.Product);

            // No answer: nothing more goes out.
            io = new MonitorChannel();
            Assert.Null(Model().Begin(io, io.Wait));
            Assert.Equal("O I1000", io.Kinds());
        }

        /// <summary>SetDirect (lines 83-102): EC 40 84 00, the count, the
        /// colors from byte 5.</summary>
        [Fact]
        public void APicture_IsSetDirect()
        {
            var map = AsusMonitorLighting.MapOf(0x1BA3, 3);
            var session = new AsusMonitorLighting.Session(map);
            var io = new MonitorChannel();
            Assert.Equal(HidLightingPaint.Sent,
                session.Paint(io, io.Wait, map.Lights, new[] { 0x112233, 0x445566, 0x778899 }));
            Assert.Equal(MonitorTest.Report("EC 40 84 00 03 11 22 33 44 55 66 77 88 99", 65),
                MonitorTest.Hex(Assert.Single(io.Sent("O"))));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>A frame holds 20 colors: a monitor that reports more
        /// lights 20.</summary>
        [Fact]
        public void AMonitorReportingMoreThan20_Lights20()
        {
            Assert.Equal(20, AsusMonitorLighting.MapOf(0x1BA3, 0xFF).Lights.Count);
            var frame = AsusMonitorLighting.DirectFrame(Enumerable.Repeat(0xABCDEF, 30).ToArray());
            Assert.Equal(65, frame.Length);
            Assert.Equal(20, frame[4]);
            Assert.Equal("AB CD EF", MonitorTest.Hex(frame, 62, 3));
        }
    }
}
