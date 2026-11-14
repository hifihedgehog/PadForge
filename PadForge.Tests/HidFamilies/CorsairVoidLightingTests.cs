using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Corsair Void Pro headsets against OpenRGB's CorsairVoidController.
    /// Every expected byte is written out from CorsairVoidController.cpp and .h.</summary>
    public class CorsairVoidLightingTests
    {
        private static HidCollectionInfo Collection(ushort product, ushort page = 0xFFC5, ushort usage = 1)
            => new()
            {
                Path = $@"\\?\hid#vid_1b1c&pid_{product:x4}&mi_03#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = 0x1B1C, ProductId = product, UsagePage = page, Usage = usage, InterfaceNumber = 3,
                OutputReportLength = 20, InputReportLength = 20,
            };

        /// <summary>OpenRGB registers the four on page FFC5, usage 1, any interface
        /// (CorsairVoidControllerDetect.cpp:25-35 and 55-58). SignalRGB's plugins keep
        /// the IDs OpenRGB lacks.</summary>
        [Theory]
        [InlineData(0x0A14, "Corsair Void Pro Wireless")]
        [InlineData(0x0A1A, "Corsair Void Pro Wireless")]
        [InlineData(0x0A17, "Corsair Void Pro USB")]
        [InlineData(0x0A1D, "Corsair Void Pro USB")]
        public void EachRegistration_IsTheFamilys_OnItsLightingCollection(int product, string name)
        {
            var model = CorsairVoidLighting.Instance.Match(Collection((ushort)product));
            Assert.Equal(name, model.Name);
            Assert.Equal(new[] { VendorPrograms.CorsairIcue }, model.Programs);
            Assert.Equal(new[] { 1, 2 }, model.Map.Lights.Select(l => l.Zone));
            Assert.Null(CorsairVoidLighting.Instance.Match(Collection((ushort)product, page: 0xFF00)));
            Assert.Null(CorsairVoidLighting.Instance.Match(Collection((ushort)product, usage: 2)));
            Assert.Null(CorsairVoidLighting.Instance.Match(Collection((ushort)product) with { OutputReportLength = 3 }));
            Assert.DoesNotContain(PluginDeviceLighting.Devices, d => d.VendorId == 0x1B1C && d.ProductId == product);
            Assert.Null(PluginDeviceLighting.Instance.Match(Collection((ushort)product)));
        }

        [Fact]
        public void TheIdsOpenRgbLacks_StayThePlugins()
        {
            Assert.Null(CorsairVoidLighting.Instance.Match(Collection(0x0A19)));
            Assert.Contains(PluginDeviceLighting.Devices, d => d.VendorId == 0x1B1C && d.ProductId == 0x0A19);
            Assert.Contains(PluginDeviceLighting.Devices, d => d.VendorId == 0x1B1C && d.ProductId == 0x1B23);
        }

        /// <summary>The lights are taken with C8 01 before the first picture, and a
        /// picture is CB 06 with the left earcup's registers 1C, 16 and 17 and the
        /// right's 1D, 18 and 19 (SetLightingControl and SetColors,
        /// CorsairVoidController.cpp:61-113). They are taken again only once a second
        /// has passed (CORSAIR_VOID_LIGHTING_REFRESH_MS, CorsairVoidController.h:34).</summary>
        [Fact]
        public void APicture_TakesTheLightsFirst_AndAgainAfterASecond()
        {
            long now = 5000;
            var session = new CorsairVoidLighting.Session(() => now);
            var io = new FakeReportChannel { OutputReportLength = 20 };
            var lights = CorsairVoidLighting.Lights();
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new[] { 0x112233, 0x445566 }));
            Assert.Equal(new[] { "C8 01 00", "CB 06 1C 11 16 22 17 33 1D 44 18 55 19 66 00 00 00 00 00 00" },
                Calls(io).Select(c => Hex(c.Bytes)));
            now += 999;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new[] { 0x010203, 0x040506 }));
            Assert.Equal(3, io.Calls.Count);
            Assert.Equal("CB 06 1C 01 16 02 17 03 1D 04 18 05 19 06", Hex(Calls(io)[2].Bytes, 14));
            now += 1;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new[] { 0x010203, 0x040506 }));
            Assert.Equal("C8 01 00", Hex(Calls(io)[3].Bytes));
            Assert.Equal(5, io.Calls.Count);
        }

        /// <summary>C8 00 gives the lights back to the firmware (the destructor,
        /// CorsairVoidController.cpp:24-36), and is the crash frame once they were
        /// taken. A session that never painted gives nothing back.</summary>
        [Fact]
        public void TheHandBack_GivesTheLightsBackToTheFirmware()
        {
            var io = new FakeReportChannel { OutputReportLength = 20 };
            var idle = new CorsairVoidLighting.Session(() => 0);
            Assert.True(idle.HandBack(io, _ => { }));
            Assert.Empty(io.Calls);

            var session = new CorsairVoidLighting.Session(() => 0);
            session.Paint(io, _ => { }, CorsairVoidLighting.Lights(), new[] { 0, 0 });
            Assert.Equal("C8 00 00", Hex(Assert.Single(session.CrashFrames)));
            // The same frames from one read to the next: the backend tells an
            // unchanged record by reference (HidLightingBackend.Track).
            Assert.Same(session.CrashFrames, session.CrashFrames);
            io.Calls.Clear();
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(new[] { "C8 00 00" }, Calls(io).Select(c => Hex(c.Bytes)));

            var broken = new FakeReportChannel { FailWrites = true };
            Assert.Equal(HidLightingPaint.Failed, new CorsairVoidLighting.Session(() => 0)
                .Paint(broken, _ => { }, CorsairVoidLighting.Lights(), new[] { 0, 0 }));
        }

        [Fact]
        public void TheSharedTable_HoldsTheFamily_BeforeThePluginRows()
        {
            var all = HidLightingFamilies.All.ToList();
            Assert.True(all.IndexOf(CorsairVoidLighting.Instance) >= 0);
            Assert.True(all.IndexOf(CorsairVoidLighting.Instance) < all.IndexOf(PluginDeviceLighting.Instance));
        }
    }
}
