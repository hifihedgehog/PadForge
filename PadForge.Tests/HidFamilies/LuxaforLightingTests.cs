using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Luxafor Flag against OpenRGB's LuxaforController (#557).</summary>
    public class LuxaforLightingTests
    {
        [Fact]
        public void EveryLight_GoesOutInItsOwnDirectReport()
        {
            var model = DeskLightKit.ModelOf(LuxaforLighting.Instance, "LuxaforController");
            // Flag 3, rear 3 (RGBController_Luxafor.cpp:125-173).
            Assert.Equal(new[] { 1, 1, 1, 2, 2, 2 }, model.Map.Lights.Select(l => l.Zone));
            var (io, session) = DeskLightKit.Painted(model, Distinct(6));
            // SendPacket (LuxaforController.cpp:71-83): 00, mode 01, the
            // light from 1, red, green, blue, 100, 0, 0.
            Assert.Equal(new[]
            {
                "00 01 01 01 02 03 64 00 00",
                "00 01 02 02 04 06 64 00 00",
                "00 01 03 03 06 09 64 00 00",
                "00 01 04 04 08 0C 64 00 00",
                "00 01 05 05 0A 0F 64 00 00",
                "00 01 06 06 0C 12 64 00 00",
            }, Calls(io).Select(c => Hex(c.Bytes)));
            DeskLightKit.HandsBackNothing(io, session);
        }
    }

    /// <summary>The Luxafor Flag through the shared worker: a claim sends
    /// its frames and the claim's end sends nothing more (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class LuxaforWorkerTests
    {
        [Fact]
        public void TheLuxafor_IsLitThroughTheWorker()
        {
            using var rig = new KeyboardFamilyRig { OutputLength = 9 };
            var r = OpenRgbRegistrations.Of(LuxaforLighting.OpenRgbFamily).Single();
            var c = KeyboardFamilyRig.Collection((ushort)r.VendorId, (ushort)r.ProductId, 0xFF00, 1, output: 9);
            using var backend = rig.Lit(new IHidLightingFamily[] { LuxaforLighting.Instance }, c, 0x102030);
            var outputs = rig.Log.Of('O');
            Assert.Equal(6, outputs.Count);
            Assert.Equal("00 01 01 10 20 30 64 00 00", Hex(outputs[0]));
            Assert.Equal("00 01 06 10 20 30 64 00 00", Hex(outputs[5]));
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }
    }
}
