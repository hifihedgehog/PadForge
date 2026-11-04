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
    /// <summary>The ViewSonic monitors against OpenRGB's ViewSonicController
    /// (#557).</summary>
    public class ViewSonicMonitorLightingTests
    {
        private static OpenRgbRegistration Registration(string name)
            => OpenRgbRegistrations.Of(ViewSonicMonitorLighting.OpenRgbFamily).Single(r => r.Name == name);

        [Fact]
        public void TheModels_HoldAndSpaceTheirWrites_AndNeedTheirFrameLength()
        {
            var qg = Registration("ViewSonic Monitor XG270QG");
            var qg271 = Registration("ViewSonic Monitor XG271QG");
            var qc = Registration("ViewSonic Monitor XG270QC");
            Assert.NotNull(ViewSonicMonitorLighting.Instance.Match(MonitorTest.CollectionOf(qg, feature: 32)));
            Assert.Null(ViewSonicMonitorLighting.Instance.Match(MonitorTest.CollectionOf(qg, feature: 31)));
            Assert.NotNull(ViewSonicMonitorLighting.Instance.Match(MonitorTest.CollectionOf(qg271, feature: 32)));
            Assert.NotNull(ViewSonicMonitorLighting.Instance.Match(MonitorTest.CollectionOf(qc, feature: 167)));
            Assert.Null(ViewSonicMonitorLighting.Instance.Match(MonitorTest.CollectionOf(qc, feature: 166)));
            var model = ViewSonicMonitorLighting.Instance.Match(MonitorTest.CollectionOf(qc));
            Assert.Equal(150, model.HoldMs);
            Assert.Equal(500, model.RepaintMs);
            Assert.Equal(new[] { 1, 2 }, model.Map.Lights.Select(l => l.Zone));
            Assert.All(model.Map.Lights, l => Assert.Equal(LightKind.Zone, l.Kind));
        }

        /// <summary>SendModeComplete (VS_XG270QG_Controller.cpp:57-94 and
        /// VS_XG270QC_Controller.cpp:70-105): 02, then 01 R G B 00 0A 00 for
        /// the base and 01 R G B 00 0A for the rear, 00 01 at 14 and 15, 32
        /// bytes on a QG, 167 on a QC.</summary>
        [Fact]
        public void APicture_IsSendModeComplete()
        {
            Assert.Equal(MonitorTest.Report("02 01 11 22 33 00 0A 00 01 44 55 66 00 0A 00 01", 32),
                MonitorTest.Hex(ViewSonicMonitorLighting.Frame(false, 0x112233, 0x445566)));
            Assert.Equal(MonitorTest.Report("02 01 11 22 33 00 0A 00 01 44 55 66 00 0A 00 01", 167),
                MonitorTest.Hex(ViewSonicMonitorLighting.Frame(true, 0x112233, 0x445566)));

            var session = new ViewSonicMonitorLighting.Session(qc: false);
            var io = new MonitorChannel();
            var lights = ViewSonicMonitorLighting.Lights();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights.Reverse().ToArray(), new[] { 0x445566, 0x112233 }));
            Assert.Equal(MonitorTest.Report("02 01 11 22 33 00 0A 00 01 44 55 66 00 0A 00 01", 32),
                MonitorTest.Hex(Assert.Single(io.Sent("F"))));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new MonitorChannel { FailAfter = 0 }, _ => { }, lights, new[] { 1, 2 }));
        }
    }
}
