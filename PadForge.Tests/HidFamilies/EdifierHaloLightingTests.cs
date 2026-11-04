using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Edifier Halo Pixelbar against OpenRGB's
    /// EdifierHaloController (#557).</summary>
    public class EdifierHaloLightingTests
    {
        [Fact]
        public void ThePackets_CarryOpenRgbsChecksum()
        {
            // SendEdifierPacket (EdifierHaloController.cpp:168-205): the
            // checksum is the low byte of the sum from AA to the payload's end.
            Assert.Equal("2E AA EC 6B 00 07 00 00 00 00 01 FF FF 07 00 00", Hex(EdifierHaloLighting.PowerOn(), 16));
            Assert.Equal("2E AA EC 6B 00 07 13 03 11 22 33 3C 01 C1 00 00", Hex(EdifierHaloLighting.Ambient(0x112233), 16));
            Assert.Equal("2E AA EC EF 00 04 03 AB CD EF F3 00 00 00 00 00", Hex(EdifierHaloLighting.Screen(0xABCDEF), 16));
            Assert.Equal(64, EdifierHaloLighting.PowerOn().Length);
        }

        [Fact]
        public void APicture_SendsTheLightsWhoseColorChanged_AndHoldsAsAStoringDevice()
        {
            var model = DeskLightKit.ModelOf(EdifierHaloLighting.Instance, "EdifierHaloController");
            Assert.Equal(new[] { 1, 2 }, model.Map.Lights.Select(l => l.Zone));
            Assert.All(model.Map.Lights, l => Assert.Equal(LightKind.Zone, l.Kind));
            // Every mode saves itself (MODE_FLAG_AUTOMATIC_SAVE), so the
            // picture is held and spaced as a stored write.
            Assert.Equal(LenovoLegionLighting.HoldMs, model.HoldMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, model.RepaintMs);
            var (io, session) = DeskLightKit.Painted(model, new[] { 0x112233, 0xABCDEF });
            var calls = Calls(io);
            Assert.Equal(3, calls.Count);
            Assert.Equal(Hex(EdifierHaloLighting.PowerOn()), Hex(calls[0].Bytes));
            Assert.Equal(Hex(EdifierHaloLighting.Ambient(0x112233)), Hex(calls[1].Bytes));
            Assert.Equal(Hex(EdifierHaloLighting.Screen(0xABCDEF)), Hex(calls[2].Bytes));
            // Only the display's color changed.
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x112233, 0x010203 }));
            calls = Calls(io);
            Assert.Equal(4, calls.Count);
            Assert.Equal(Hex(EdifierHaloLighting.Screen(0x010203)), Hex(calls[3].Bytes));
            // Only the light effects changed: the power command and the effect.
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x445566, 0x010203 }));
            calls = Calls(io);
            Assert.Equal(6, calls.Count);
            Assert.Equal(Hex(EdifierHaloLighting.PowerOn()), Hex(calls[4].Bytes));
            Assert.Equal(Hex(EdifierHaloLighting.Ambient(0x445566)), Hex(calls[5].Bytes));
            DeskLightKit.HandsBackNothing(io, session);
        }

        [Fact]
        public void AFailedWrite_FailsThePicture()
        {
            var model = DeskLightKit.ModelOf(EdifierHaloLighting.Instance, "EdifierHaloController");
            var io = new FakeReportChannel { OutputReportLength = 64, FailWrites = true };
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, new[] { 1, 2 }));
            io.FailWrites = false;
            // Both lights go out again on the next picture.
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 1, 2 }));
            Assert.Equal(3, io.Calls.Count);
        }
    }
}
