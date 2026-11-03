using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>The MSI Mystic Light keyboards against OpenRGB's
    /// MSIKeyboardController (#557): the MS-1565's SetMode frames
    /// (MSIKeyboard1565Controller.cpp:34-65 and .h:35-50) and the 64-byte
    /// keyboards' FeaturePacket_64 (MSIMysticLight64Controller.cpp:31-58 and
    /// .h:63-73), byte for byte.</summary>
    public class MsiKeyboardLightingTests
    {
        private static HidLightingModel ModelOf(OpenRgbRegistration r)
            => MsiKeyboardLighting.Instance.Match(CollectionOf(r, feature: 64));

        [Fact]
        public void EveryRegistration_FindsItsModel()
        {
            var registrations = OpenRgbRegistrations.Of(MsiKeyboardLighting.OpenRgbFamily).ToList();
            Assert.Equal(4, registrations.Count);
            foreach (var r in registrations)
            {
                var model = ModelOf(r);
                Assert.NotNull(model);
                Assert.Same(MsiKeyboardLighting.Instance, model.Family);
                Assert.Equal(r.Name, model.Name);
                Assert.Equal($"msi:1462:{r.ProductId:X4}", model.Map.Product);
                Assert.Same(model, ModelOf(r));
                Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: 64)));
            }
        }

        [Fact]
        public void TheModels_HoldAndSpaceTheirPictures_AndYieldToMysticLight()
        {
            foreach (var r in OpenRgbRegistrations.Of(MsiKeyboardLighting.OpenRgbFamily))
            {
                var model = ModelOf(r);
                Assert.Equal(150, model.HoldMs);
                Assert.Equal(500, model.RepaintMs);
                Assert.Equal(new[] { VendorPrograms.MsiMysticLight }, model.Programs);
                Assert.False(model.LitBesideWindows);
            }
            Assert.Contains(VendorPrograms.MsiMysticLight, VendorPrograms.All);
            Assert.True(VendorPrograms.MsiMysticLight.Owns("Mystic_Light_Service"));
            Assert.False(VendorPrograms.MsiMysticLight.Owns("MSI.CentralServer"));
        }

        [Fact]
        public void ACollectionOfOtherShape_IsNotTheFamilys()
        {
            var r = OpenRgbRegistrations.Of(MsiKeyboardLighting.OpenRgbFamily).First();
            Assert.Null(MsiKeyboardLighting.Instance.Match(CollectionOf(r, feature: 63)));
            Assert.Null(MsiKeyboardLighting.Instance.Match(CollectionOf(r, feature: 64) with { UsagePage = 0xFF00 }));
            Assert.Null(MsiKeyboardLighting.Instance.Match(CollectionOf(r, feature: 64) with { ProductId = 0x1600 }));
            Assert.Null(MsiKeyboardLighting.Instance.Match(null));
        }

        [Fact]
        public void TheLights_AreOpenRgbsZones()
        {
            var lights = MsiKeyboardLighting.Lights1565();
            Assert.Equal(new[] { 1, 2, 3, 4 }, lights.Select(l => l.Address));
            Assert.Equal(new[] { 1, 2, 3, 4 }, lights.Select(l => l.Zone));
            Assert.All(lights, l => Assert.Equal(LightKind.Zone, l.Kind));
            var one = Assert.Single(MsiKeyboardLighting.Lights64());
            Assert.Equal(LightKind.Zone, one.Kind);
            Assert.Equal(1, one.Zone);
        }

        /// <summary>SetMode's first report: 02 01 and the zone_map bit
        /// (MSIKeyboard1565Controller.cpp:14-21 and 44-48).</summary>
        [Fact]
        public void TheZoneReport_CarriesTheZoneBit()
        {
            Assert.Equal(Padded("02 01 01", 64), MsiKeyboardLighting.ZoneSelect(1));
            Assert.Equal(Padded("02 01 02", 64), MsiKeyboardLighting.ZoneSelect(2));
            Assert.Equal(Padded("02 01 04", 64), MsiKeyboardLighting.ZoneSelect(3));
            Assert.Equal(Padded("02 01 08", 64), MsiKeyboardLighting.ZoneSelect(4));
            Assert.Equal(Padded("02 01 0F", 64), MsiKeyboardLighting.ZoneSelect(MsiKeyboardLighting.DeviceZone));
        }

        /// <summary>FeaturePacket_MS1565 of the Static mode: 02 02, mode 1,
        /// speed 1200 = 0x04B0 low byte first, 00 00 0F 01, direction 0,
        /// key frames (0, color) and (100, color), zeros to 64 bytes.</summary>
        [Fact]
        public void TheStaticReport_IsFeaturePacketMs1565()
        {
            Assert.Equal(Padded("02 02 01 B0 04 00 00 0F 01 00 00 11 22 33 64 11 22 33", 64),
                MsiKeyboardLighting.SteadyFrame(0x112233));
        }

        [Fact]
        public void OneColor_GoesToTheWholeDevice_AndManyGoZoneByZone()
        {
            var lights = MsiKeyboardLighting.Lights1565();
            var same = MsiKeyboardLighting.Frames1565(lights, new[] { 0x0A0B0C, 0x0A0B0C, 0x0A0B0C, 0x0A0B0C });
            Assert.Equal(2, same.Count);
            Assert.Equal(Padded("02 01 0F", 64), same[0]);
            Assert.Equal(MsiKeyboardLighting.SteadyFrame(0x0A0B0C), same[1]);

            var mixed = MsiKeyboardLighting.Frames1565(lights, new[] { 0x010101, 0x020202, 0x030303, 0x040404 });
            Assert.Equal(8, mixed.Count);
            byte[] bits = { 1, 2, 4, 8 };
            for (int z = 0; z < 4; z++)
            {
                Assert.Equal(Padded("02 01 " + bits[z].ToString("X2"), 64), mixed[2 * z]);
                Assert.Equal(MsiKeyboardLighting.SteadyFrame(0x010101 * (z + 1)), mixed[2 * z + 1]);
            }
        }

        /// <summary>FeaturePacket_64 of the Static mode: 02 00, mode 1,
        /// speed 0, brightness 10, seven colors, the light's first and then
        /// SetupMode's orange, yellow, green, blue, purple and magenta
        /// (RGBController_MSIMysticLight64.cpp:149-155, RGBColor 0x00BBGGRR).</summary>
        [Fact]
        public void TheStaticReport64_IsFeaturePacket64()
        {
            Assert.Equal(
                Padded("02 00 01 00 0A 07 11 22 33 FF 50 00 FF FF 00 00 FF 00 00 00 FF 96 00 FF FF 00 FF", 64),
                MsiKeyboardLighting.SteadyFrame64(0x112233));
        }

        [Fact]
        public void TheSessions_SendTheFrames_AndHandBackNothing()
        {
            var io = new LoggingHidChannel();
            var lights = MsiKeyboardLighting.Lights1565();
            var session = new MsiKeyboardLighting.Session1565();
            Assert.True(session.Feature);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new[] { 1, 2, 3, 4 }));
            Assert.Equal(MsiKeyboardLighting.Frames1565(lights, new[] { 1, 2, 3, 4 }), io.Sent("F"));
            Assert.Empty(session.CrashFrames);
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(8, io.Log.Count);

            var io64 = new LoggingHidChannel();
            var s64 = new MsiKeyboardLighting.Session64();
            Assert.Equal(HidLightingPaint.Sent, s64.Paint(io64, io64.Wait, MsiKeyboardLighting.Lights64(), new[] { 0x445566 }));
            Assert.Equal(new[] { MsiKeyboardLighting.SteadyFrame64(0x445566) }, io64.Sent("F"));
            Assert.True(s64.HandBack(io64, io64.Wait));
            Assert.Empty(s64.CrashFrames);
        }

        [Fact]
        public void AFailedWrite_FailsThePicture()
        {
            var io = new LoggingHidChannel { FailAfter = 1 };
            Assert.Equal(HidLightingPaint.Failed, new MsiKeyboardLighting.Session1565()
                .Paint(io, io.Wait, MsiKeyboardLighting.Lights1565(), new[] { 1, 2, 3, 4 }));
            Assert.Single(io.Log);
            var io64 = new LoggingHidChannel { FailWrites = true };
            Assert.Equal(HidLightingPaint.Failed, new MsiKeyboardLighting.Session64()
                .Paint(io64, io64.Wait, MsiKeyboardLighting.Lights64(), new[] { 1 }));
        }
    }
}
