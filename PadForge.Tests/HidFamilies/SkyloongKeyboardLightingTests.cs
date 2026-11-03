using System.Linq;
using System.Text;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Skyloong GK104 Pro against OpenRGB's SkyloongController (#557).</summary>
    public class SkyloongKeyboardLightingTests
    {
        private static OpenRgbRegistration Registration()
            => OpenRgbRegistrations.Of(SkyloongKeyboardLighting.OpenRgbFamily).Single();

        private static HidLightingModel Model()
            => SkyloongKeyboardLighting.Instance.Match(CollectionFor(Registration(), output: 65));

        [Fact]
        public void TheRegistration_AnswersOnTheCollectionThatTakesAFrame()
        {
            var r = Registration();
            // SkyloongControllerDetect.cpp:20-22 and 45.
            Assert.Equal((0x1EA7, 0x0907, 1), (r.VendorId, r.ProductId, r.Interface));
            Assert.Equal("Skyloong GK104 Pro", Model().Name);
            Assert.Same(Model(), HidLightingFamilies.Match(CollectionFor(r, output: 65)));
            // The interface's other collections carry no 65-byte output report.
            Assert.Null(SkyloongKeyboardLighting.Instance.Match(CollectionFor(r, output: 0)));
            Assert.Null(SkyloongKeyboardLighting.Instance.Match(CollectionFor(r, output: 9)));
            Assert.Empty(Model().Programs);
        }

        [Fact]
        public void TheCrc_IsCcittFalse()
        {
            // The CRC-16/CCITT-FALSE check value: "123456789" gives 0x29B1
            // (poly 0x1021, init 0xFFFF, no reflection, no final xor, the
            // loop of SkyloongGK104ProController.cpp:118-140).
            Assert.Equal(0x29B1, SkyloongKeyboardLighting.Crc(Encoding.ASCII.GetBytes("123456789")));
        }

        [Fact]
        public void TheCommands_CarryTheirCrcInBytesSevenAndEight()
        {
            var ping = SkyloongKeyboardLighting.Command(0x0C, 0x00);
            Assert.Equal(65, ping.Length);
            var zeroed = (byte[])ping.Clone();
            zeroed[7] = zeroed[8] = 0;
            ushort crc = SkyloongKeyboardLighting.Crc(zeroed);
            Assert.Equal((byte)crc, ping[7]);
            Assert.Equal((byte)(crc >> 8), ping[8]);
            Assert.Equal("00 0C 00", Hex(ping, 3));
            Assert.Equal("00 0B 05", Hex(SkyloongKeyboardLighting.Command(0x0B, 0x05), 3));
            Assert.Equal("00 1A 02", Hex(SkyloongKeyboardLighting.Command(0x1A, 0x02), 3));
            // A define piece: address 0x0118 and length 56 in bytes 3 to 6
            // (SetLEDefine, lines 103-107), data from byte 9.
            var define = SkyloongKeyboardLighting.Define(0x0118, Enumerable.Repeat((byte)0xAB, 56).ToArray());
            Assert.Equal("00 1A 01 18 01 00 38", Hex(define, 7));
            Assert.All(define.Skip(9), b => Assert.Equal(0xAB, b));
        }

        [Fact]
        public void APicture_IsTenPiecesAndTheSave_EachLedAtFourTimesItsValue()
        {
            var lights = Model().Map.Lights;
            var colors = Distinct(lights.Count);
            var picture = SkyloongKeyboardLighting.Picture(lights, colors);
            // 528 bytes in 56-byte pieces: nine and one of 24 (lines 81-90), then 1A 02.
            Assert.Equal(11, picture.Count);
            Assert.Equal(new[] { 56, 56, 56, 56, 56, 56, 56, 56, 56, 24 }, picture.Take(10).Select(r => (int)r[6]));
            Assert.Equal(new[] { 0, 56, 112, 168, 224, 280, 336, 392, 448, 504 },
                picture.Take(10).Select(r => r[3] | r[4] << 8));
            Assert.Equal("00 1A 02", Hex(picture[10], 3));
            // Escape at value 0, F1 at 2 (RGBController_SkyloongGK104Pro.cpp:25).
            int escape = lights.ToList().FindIndex(l => l.Usage == 0x29);
            Assert.Equal(0, lights[escape].Address);
            Assert.Equal(new[] { (byte)(colors[escape] >> 16), (byte)(colors[escape] >> 8), (byte)colors[escape], (byte)127 },
                picture[0].Skip(9).Take(4));
            Assert.Equal(2, lights.First(l => l.Usage == 0x3A).Address);
            // Number Pad . at 130: byte 520, piece 9 at 520 - 504.
            int dot = lights.ToList().FindIndex(l => l.Usage == 0x63);
            Assert.Equal(130, lights[dot].Address);
            Assert.Equal((byte)(colors[dot] >> 16), picture[9][9 + 16]);
        }

        [Fact]
        public void ASession_GoesOnline_AndTheHandBackTakesItOffline()
        {
            var model = Model();
            var io = new FakeReportChannel { OutputReportLength = 65 };
            var session = model.Begin(io, _ => { });
            Assert.NotNull(session);
            Assert.Equal(new[] { "00 0C", "00 0B 05", "00 0C" },
                Calls(io).Select(c => Hex(c.Bytes, c.Bytes[1] == 0x0B ? 3 : 2)));
            Assert.False(session.Feature);
            Assert.Equal("00 0B 04", Hex(session.CrashFrames.Single(), 3));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, Distinct(model.Map.Lights.Count)));
            Assert.Equal(3 + 11, io.Calls.Count);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal("00 0B 04", Hex(Calls(io)[^1].Bytes, 3));
            Assert.Empty(session.CrashFrames);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(3 + 11 + 1, io.Calls.Count);
            io.FailWrites = true;
            Assert.Null(model.Begin(io, _ => { }));
        }
    }
}
