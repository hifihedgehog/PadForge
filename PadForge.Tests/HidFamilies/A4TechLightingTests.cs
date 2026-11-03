using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>A4Tech's Bloody devices against OpenRGB's A4TechController
    /// (#557): SendControlPacket and SetLEDDirect
    /// (BloodyB820RController.cpp:29-110), InitDevice and SetLedsDirect
    /// (BloodyMouseController.cpp:58-84), and the light tables.</summary>
    public class A4TechLightingTests
    {
        private static HidLightingModel ModelOf(ushort pid)
            => A4TechLighting.Instance.Match(CollectionOf(
                OpenRgbRegistrations.Of(A4TechLighting.OpenRgbFamily).Single(r => r.ProductId == pid), feature: 64));

        [Fact]
        public void EveryRegistration_FindsItsModel()
        {
            var registrations = OpenRgbRegistrations.Of(A4TechLighting.OpenRgbFamily).ToList();
            Assert.Equal(6, registrations.Count);
            foreach (var r in registrations)
            {
                var model = A4TechLighting.Instance.Match(CollectionOf(r, feature: 64));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: 64)));
                Assert.Null(A4TechLighting.Instance.Match(CollectionOf(r, feature: 63)));
                Assert.Equal(50, model.RepaintMs);
                Assert.Empty(model.Programs);
            }
            Assert.Equal(104, ModelOf(0xFA10).Map.Lights.Count);
            Assert.Equal(15, ModelOf(0x37EA).Map.Lights.Count);
            Assert.Equal(15, ModelOf(0x79EF).Map.Lights.Count);
            Assert.Equal(15, ModelOf(0x3666).Map.Lights.Count);
            Assert.Equal(15, ModelOf(0x39B6).Map.Lights.Count);
            Assert.Equal(10, ModelOf(0xFA60).Map.Lights.Count);
        }

        [Fact]
        public void TheLights_AreOpenRgbsTables()
        {
            var keys = A4TechLighting.KeyboardLights();
            Assert.Equal(Enumerable.Range(0, 104), keys.Select(l => l.Address));
            Assert.Equal((ushort)0x29, keys[0].Usage);
            Assert.All(keys, l => Assert.True(l.HasPosition));
            Assert.Equal(LightLabel.Fn, keys.Single(l => l.Kind == LightKind.Other).Label);
            // W60 Pro: the wheel (14) a zone, the mid line's eight and the
            // rear's five edge lights, the logo (10).
            var w60 = A4TechLighting.MouseLights(0x37EA);
            Assert.Equal(new[] { 14, 0, 1, 2, 9, 3, 4, 5, 6, 10, 8, 7, 13, 12, 11 }, w60.Select(l => l.Address));
            Assert.Equal(LightKind.Zone, w60[0].Kind);
            Assert.Equal(LightKind.Logo, w60[9].Kind);
            Assert.Equal(13, w60.Count(l => l.Kind == LightKind.Edge));
            var pad = A4TechLighting.MouseLights(0xFA60);
            Assert.All(pad, l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Null(A4TechLighting.MouseLights(0x1234));
        }

        [Fact]
        public void TheControlReports_TakeAndLetGo()
        {
            var take = A4TechLighting.ControlFrames(1);
            Assert.Equal(Padded("07 03 06 01 00 00 00 00", 64), take[0]);
            Assert.Equal(Padded("07 03 06 00 00 00 00 00 01", 64), take[1]);
            var release = A4TechLighting.ControlFrames(0);
            Assert.Equal(Padded("07 03 06 01 00 00 00 00", 64), release[0]);
            Assert.Equal(Padded("07 03 06 00", 64), release[1]);
        }

        /// <summary>r1 r2 g1 g2 b1 b2 with modes 07 to 0C; key 0 at byte 6 of
        /// the first, key 58 at byte 6 of the second, key 103 at byte 51.</summary>
        [Fact]
        public void TheKeyboardColors_GoInSixReports()
        {
            var keys = A4TechLighting.KeyboardLights();
            var colors = keys.Select(l => l.Address switch { 0 => 0x112233, 58 => 0x445566, 103 => 0x778899, _ => 0 }).ToList();
            var frames = A4TechLighting.KeyboardFrames(keys, colors);
            Assert.Equal(new byte[] { 7, 8, 9, 10, 11, 12 }, frames.Select(f => f[3]));
            Assert.All(frames, f => Assert.Equal("07 03 06", Hex(f, 3)));
            Assert.Equal(0x11, frames[0][6]);
            Assert.Equal(0x22, frames[2][6]);
            Assert.Equal(0x33, frames[4][6]);
            Assert.Equal(0x44, frames[1][6]);
            Assert.Equal(0x66, frames[5][6]);
            Assert.Equal(0x77, frames[1][6 + 45]);
            Assert.Equal(0x99, frames[5][6 + 45]);
            Assert.Equal(0x11 + 0x22 + 0x33 + 0x44 + 0x55 + 0x66 + 0x77 + 0x88 + 0x99,
                frames.Sum(f => f.Skip(6).Sum(b => b)));
        }

        [Fact]
        public void TheMouseColors_GoInOneReport()
        {
            var lights = A4TechLighting.MouseLights(0x3666);
            var colors = lights.Select(l => l.Address == 7 ? 0xAABBCC : 0).ToList();
            var frame = A4TechLighting.MouseFrame(lights, colors);
            Assert.Equal("07 03 06 02 00 00 00 00", Hex(frame, 8));
            Assert.Equal("AA BB CC", Hex(frame.Skip(8 + 21).Take(3).ToArray()));
            Assert.Equal(0xAA + 0xBB + 0xCC, frame.Skip(8).Sum(b => b));
        }

        [Fact]
        public void ASession_TakesControlOnce_AndLetsGoOnHandBack()
        {
            var io = new LoggingHidChannel();
            var session = new A4TechLighting.Session(keyboard: true);
            var keys = A4TechLighting.KeyboardLights();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(io.Log);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, keys, new int[104]));
            Assert.Equal(8, io.Log.Count);
            Assert.Equal(A4TechLighting.ControlFrames(1), io.Sent("F").Take(2));
            Assert.Equal(A4TechLighting.ControlFrames(0), session.CrashFrames);
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, keys, new int[104]));
            Assert.Equal(6, io.Log.Count);
            io.Log.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(A4TechLighting.ControlFrames(0), io.Sent("F"));
            Assert.Empty(session.CrashFrames);

            var mouse = new A4TechLighting.Session(keyboard: false);
            var io2 = new LoggingHidChannel();
            var lights = A4TechLighting.MouseLights(0x37EA);
            Assert.Equal(HidLightingPaint.Sent, mouse.Paint(io2, io2.Wait, lights, new int[15]));
            Assert.Equal(3, io2.Log.Count);
            Assert.Equal(A4TechLighting.MouseFrame(lights, new int[15]), io2.Log[2].Bytes);
            var failing = new LoggingHidChannel { FailAfter = 1 };
            var s3 = new A4TechLighting.Session(keyboard: false);
            Assert.Equal(HidLightingPaint.Failed, s3.Paint(failing, io2.Wait, lights, new int[15]));
            Assert.NotEmpty(s3.CrashFrames);
            Assert.False(s3.HandBack(new LoggingHidChannel { FailWrites = true }, io2.Wait));
        }
    }
}
