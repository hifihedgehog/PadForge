using System.Collections.Generic;
using System.Linq;
using System.Text;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Witmod GK8110 and GK8120 against OpenRGB's WitmodKeyboardController (#557).</summary>
    public class WitmodKeyboardLightingTests
    {
        private static OpenRgbRegistration Registration()
            => OpenRgbRegistrations.Of(WitmodKeyboardLighting.OpenRgbFamily).Single();

        private static HidLightingModel Model()
            => WitmodKeyboardLighting.Instance.Match(CollectionFor(Registration(), output: 64, input: 64));

        private static byte[] Answer(string info)
        {
            // 01 0D, padding, then the string (WitmodKeyboardController.h:21-30,
            // WitmodKeyboardController.cpp:106-132).
            var answer = new byte[64];
            answer[0] = 0x01;
            answer[1] = 0x0D;
            Encoding.ASCII.GetBytes(info).CopyTo(answer, 5);
            return answer;
        }

        [Fact]
        public void TheRegistration_AnswersOnTheCollectionThatTakesAFrame()
        {
            var r = Registration();
            // WitmodKeyboardControllerDetect.cpp:22-23 and 43.
            Assert.Equal((0x0416, 0xC345, 2), (r.VendorId, r.ProductId, r.Interface));
            Assert.Same(Model(), HidLightingFamilies.Match(CollectionFor(r, output: 64)));
            Assert.Null(WitmodKeyboardLighting.Instance.Match(CollectionFor(r, output: 9)));
            Assert.Equal("Witmod Keyboard", Model().Name);
            Assert.Empty(Model().Programs);
        }

        [Fact]
        public void TheAnswer_TellsTheSizes_AsReadDeviceInfoDoes()
        {
            Assert.True(WitmodKeyboardLighting.IsFullSize(Answer("2M252,01,KB,JC,GK8120SKRGB,V1.04.04"), 64));
            Assert.False(WitmodKeyboardLighting.IsFullSize(Answer("2M252,01,KB,JC,GK8110SKRGB,V1.04.04"), 64));
            // An unknown model is the TKL (WitmodKeyboardController.cpp:157-160).
            Assert.False(WitmodKeyboardLighting.IsFullSize(Answer("SOMETHING"), 64));
            // Not the answer, or no string in it (lines 110-132).
            var effect = Answer("GK8120");
            effect[1] = 0x09;
            Assert.Null(WitmodKeyboardLighting.IsFullSize(effect, 64));
            Assert.Null(WitmodKeyboardLighting.IsFullSize(Answer(""), 64));
            Assert.Null(WitmodKeyboardLighting.IsFullSize(Answer("GK8120"), 1));
        }

        [Fact]
        public void TheMaps_AreOpenRgbsTwoLayouts_AtTheirGreenOffsets()
        {
            var tkl = WitmodKeyboardLighting.Map(false, 0xC345);
            var full = WitmodKeyboardLighting.Map(true, 0xC345);
            // gk8110_offset_values and gk8120_offset_values (RGBController_WitmodKeyboard.cpp:39-82).
            Assert.Equal(7, tkl.Lights.First(l => l.Usage == 0x29).Address);   // Escape
            Assert.Equal(415, tkl.Lights.First(l => l.Usage == 0x2C).Address); // Space
            Assert.Equal(458, tkl.Lights.First(l => l.Usage == 0x4F).Address); // Right Arrow
            Assert.DoesNotContain(tkl.Lights, l => l.Usage == 0x53);           // no Num Lock
            Assert.Equal(147, full.Lights.First(l => l.Usage == 0x53).Address);
            Assert.Equal(467, full.Lights.First(l => l.Usage == 0x63).Address); // Number Pad .
            Assert.NotEqual(tkl.Product, full.Product);
            Assert.True(full.Lights.Count > tkl.Lights.Count);
        }

        [Fact]
        public void TheReports_AreOpenRgbs()
        {
            // The info request, 01 0D (line 85-88), and the Direct mode
            // (SetMode, lines 211-240 with the Direct mode's defaults).
            Assert.Equal("01 0D 00", Hex(WitmodKeyboardLighting.Report(0x0D), 3));
            Assert.Equal("01 07 00 00 00 00 0A 04 00 00 00 00 00 00 00 00 00 00", Hex(WitmodKeyboardLighting.DirectMode(), 18));
            var tkl = WitmodKeyboardLighting.Map(false, 0xC345);
            var colors = Distinct(tkl.Lights.Count);
            var picture = WitmodKeyboardLighting.Picture(tkl.Lights, colors);
            Assert.Equal(8, picture.Count);
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }, picture.Select(r => r[4]));
            Assert.Equal(new byte[] { 0x36, 0x36, 0x36, 0x36, 0x36, 0x36, 0x36, 0x12 }, picture.Select(r => r[5]));
            Assert.All(picture, r => Assert.Equal("01 0F", Hex(r, 2)));
            // Escape at 7: red 6, green 7, blue 8 of report 0.
            int escape = tkl.Lights.ToList().FindIndex(l => l.Usage == 0x29);
            Assert.Equal(new[] { (byte)(colors[escape] >> 16), (byte)(colors[escape] >> 8), (byte)colors[escape] },
                picture[0].Skip(6).Take(3));
            // Space at 415: report 6, bytes 414 - 384 to 416 - 384.
            int space = tkl.Lights.ToList().FindIndex(l => l.Usage == 0x2C);
            Assert.Equal((byte)(colors[space] >> 8), picture[6][415 - 384]);
        }

        [Fact]
        public void ASession_AsksTheSize_SelectsDirect_AndPublishesTheFullBoardsMap()
        {
            var model = Model();
            var answers = new Queue<byte[]>(new[] { null, Answer("GK8120SKRGB,V1.04.04") });
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64, Input = () => answers.Count > 0 ? answers.Dequeue() : null };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.NotNull(session);
            Assert.Equal(new[] { "out", "in", "in", "out" }, Calls(io).Select(c => c.Kind));
            Assert.Equal("01 07 00 00 00 00 0A 04", Hex(Calls(io)[3].Bytes, 8));
            Assert.Equal(new[] { 10 }, waits);
            Assert.NotNull(session.Map);
            Assert.EndsWith(":gk8120", session.Map.Product);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), session.Map.Lights, Distinct(session.Map.Lights.Count)));
            Assert.Equal(4 + 8, io.Calls.Count);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(4 + 8, io.Calls.Count);
            Assert.Empty(session.CrashFrames);

            // No answer in ten reads: the TKL, the model's own map.
            var silent = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            var quiet = model.Begin(silent, _ => { });
            Assert.Null(quiet.Map);
            Assert.Equal(10, Calls(silent).Count(c => c.Kind == "in"));
        }
    }
}
