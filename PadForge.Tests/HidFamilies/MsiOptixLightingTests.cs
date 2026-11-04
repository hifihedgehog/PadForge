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
    /// <summary>The MSI Optix against OpenRGB's MSIOptixController (#557).</summary>
    public class MsiOptixLightingTests
    {
        [Fact]
        public void TheModel_Has12EdgeLights_OnA168ByteCollection()
        {
            var r = Assert.Single(OpenRgbRegistrations.Of(MsiOptixLighting.OpenRgbFamily));
            var model = MsiOptixLighting.Instance.Match(MonitorTest.CollectionOf(r, feature: 168));
            Assert.NotNull(model);
            Assert.Null(MsiOptixLighting.Instance.Match(MonitorTest.CollectionOf(r, feature: 167)));
            Assert.Equal(12, model.Map.Lights.Count);
            Assert.All(model.Map.Lights, l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(50, model.RepaintMs);
        }

        /// <summary>SetDirect (MSIOptixController.cpp:73-132): 72, two blocks
        /// of 01 FF FF FF 00 64 00 FF 00 80 00, the twelve colors from
        /// 95.</summary>
        [Fact]
        public void APicture_IsSetDirect()
        {
            var colors = Enumerable.Range(1, 12).Select(i => 0x010203 * i).ToArray();
            var expected = new byte[168];
            expected[0] = 0x72;
            MonitorTest.Bytes("01 FF FF FF 00 64 00 FF 00 80 00").CopyTo(expected, 1);
            MonitorTest.Bytes("01 FF FF FF 00 64 00 FF 00 80 00").CopyTo(expected, 12);
            for (int i = 0; i < 12; i++)
            {
                expected[95 + 3 * i] = (byte)(1 * (i + 1));
                expected[96 + 3 * i] = (byte)(2 * (i + 1));
                expected[97 + 3 * i] = (byte)(3 * (i + 1));
            }
            Assert.Equal(MonitorTest.Hex(expected), MonitorTest.Hex(MsiOptixLighting.Frame(colors)));

            var session = new MsiOptixLighting.Session();
            var io = new MonitorChannel();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, MsiOptixLighting.Lights(), colors));
            Assert.Equal(MonitorTest.Hex(expected), MonitorTest.Hex(Assert.Single(io.Sent("F"))));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new MonitorChannel { FailAfter = 0 }, _ => { }, MsiOptixLighting.Lights(), colors));
        }
    }
}
