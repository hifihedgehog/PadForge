using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The NZXT Lift against OpenRGB's NZXTMouseController (#557).</summary>
    public class NzxtMouseLightingTests
    {
        private static HidLightingModel Model()
            => NzxtMouseLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(NzxtMouseLighting.OpenRgbFamily).Single(), output: 64, input: 64));

        [Fact]
        public void TheDirectReport_IsOpenRgbs()
        {
            // 43 AE 00 10 02 3F, 06 at 24, then colors[2], [1], [0], [3],
            // [4], [5] at 25, 29, 33, 37, 41, 45 (NZXTMouseController.cpp:61-112).
            var lights = NzxtMouseLighting.Lights();
            var colors = new[] { 0x000001, 0x000002, 0x000003, 0x000004, 0x000005, 0x000006 };
            var expected = Bytes(64, (0, 0x43), (1, 0xAE), (3, 0x10), (4, 0x02), (5, 0x3F), (24, 0x06),
                (27, 0x03), (31, 0x02), (35, 0x01), (39, 0x04), (43, 0x05), (47, 0x06));
            Assert.Equal(Hex(expected), Hex(NzxtMouseLighting.Frame(lights, colors)));
            var full = NzxtMouseLighting.Frame(lights, Enumerable.Repeat(0xA1B2C3, 6).ToArray());
            foreach (int at in new[] { 25, 29, 33, 37, 41, 45 })
                Assert.Equal("A1 B2 C3", Hex(full.Skip(at).ToArray(), 3));
            Assert.Equal("43 81 00 01 00", Hex(NzxtMouseLighting.FirmwareRequest(), 5));
            Assert.Equal(64, NzxtMouseLighting.FirmwareRequest().Length);
        }

        [Fact]
        public void TheLights_AreTwoZonesOfThree()
        {
            var lights = NzxtMouseLighting.Lights();
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, lights.Select(l => l.Address));
            Assert.Equal(new[] { 1, 1, 1, 2, 2, 2 }, lights.Select(l => l.Zone));
            Assert.Same(VendorPrograms.Nzxt, Model().Programs);
        }

        [Fact]
        public void ASession_AsksForTheFirmware_UntilTheAnswer_WithinTheBound()
        {
            var model = Model();
            var answers = new Queue<byte[]>(new[] { new byte[] { 0x43, 0x80 }, null, new byte[] { 0x43, 0x86, 0x00, 0x01 } });
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64, Input = () => answers.Count > 0 ? answers.Dequeue() : null };
            var session = model.Begin(io, _ => { });
            Assert.NotNull(session);
            Assert.Equal(new[] { "out", "in", "in", "in" }, Calls(io).Select(c => c.Kind));
            Assert.Equal("43 81 00 01", Hex(Calls(io)[0].Bytes, 4));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, Distinct(6)));
            Assert.Equal(0xAE, Calls(io).Last().Bytes[1]);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(5, io.Calls.Count);

            // A mouse that never answers gets no session after the bound.
            var silent = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            Assert.Null(model.Begin(silent, _ => { }));
            Assert.Equal(NzxtMouseLighting.AnswerReads, Calls(silent).Count(c => c.Kind == "in"));
            // A request that does not go out reads nothing.
            var broken = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64, FailWrites = true };
            Assert.Null(model.Begin(broken, _ => { }));
            Assert.Empty(broken.Calls);
        }
    }
}
