using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The EVision family against OpenRGB's
    /// EVisionKeyboardController, both generations (#557). Expected bytes are
    /// written out from the reference lines named.</summary>
    public class EVisionKeyboardLightingTests
    {
        private static string Hex(IEnumerable<byte> bytes) => CherryKeyboardLightingTests.Hex(bytes);

        private static HidLightingModel ModelNamed(string name)
            => EVisionKeyboardLighting.Instance.Match(CherryKeyboardLightingTests.CollectionOf(
                OpenRgbRegistrations.Of("EVisionKeyboardController").Single(r => r.Name == name)));

        /// <summary>EVisionKeyboardControllerDetect.cpp:69-81 and
        /// EVisionV2KeyboardControllerDetect.cpp:89-90: 15 products.</summary>
        [Fact]
        public void EveryRegistration_IsAModel()
        {
            var registrations = OpenRgbRegistrations.Of("EVisionKeyboardController").ToList();
            Assert.Equal(15, registrations.Count);
            foreach (var r in registrations)
            {
                var collection = CherryKeyboardLightingTests.CollectionOf(r);
                var model = EVisionKeyboardLighting.Instance.Match(collection);
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(collection));
                bool second = r.Detector is "DetectEVisionV2Keyboards" or "DetectEndorfyKeyboards";
                Assert.Equal(second ? 0 : LenovoLegionLighting.HoldMs, model.HoldMs);
                // A byte after 200 ms with nothing sent, a byte included
                // (RGBController_EVisionV2Keyboard.cpp:1009-1031).
                Assert.Equal(second ? 200 : 0, model.KeepAliveMs);
                Assert.Equal(second ? 200 : 0, model.KeepAliveIdleMs);
                Assert.Equal(0, model.RecheckMs);
            }
            Assert.Contains(EVisionKeyboardLighting.Instance, HidLightingFamilies.All);
        }

        [Fact]
        public void TheMaps_AreOpenRgbs()
        {
            // RGBController_EVisionKeyboard.cpp:17-23 and 310-334: 126 LEDs, 6 by 23.
            var first = ModelNamed("EVision Keyboard 0C45:5004").Map;
            Assert.Equal(126, first.Lights.Count);
            Assert.Equal((22f, 1f), (first.Lights[41].X, first.Lights[41].Y));
            // RGBController_EVisionV2Keyboard.cpp:21-156: 106 named LEDs,
            // Escape first at row 0 column 0.
            var v2 = ModelNamed("EVision Keyboard 320F:5041").Map;
            Assert.Equal(106, v2.Lights.Count);
            Assert.Equal((ushort)0x29, v2.Lights[0].Usage);
            Assert.Equal((ushort)0x3A, v2.Lights[1].Usage);
            Assert.All(v2.Lights, l => Assert.True(l.HasPosition));
            // led_endorfy, 104 LEDs (lines 158-269).
            var endorfy = ModelNamed("Endorfy Omnis").Map;
            Assert.Equal(104, endorfy.Lights.Count);
            Assert.Equal((ushort)0x29, endorfy.Lights[0].Usage);
            Assert.True(v2.KeyCount >= 100);
            Assert.True(endorfy.KeyCount >= 100);
        }

        /// <summary>SendKeyboardModeEx for the Custom mode
        /// (EVisionKeyboardController.cpp:92-116 and 231-259): command 06,
        /// eight bytes from byte 8, no 55 in byte 7, sum 06 + 08 + 14 + 04 = 0x26.</summary>
        [Fact]
        public void TheFirstGeneration_SetsCustomThenWritesItsColors()
        {
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            var model = ModelNamed("EVision Keyboard 320F:5000");
            var session = model.Begin(io, _ => { });
            var colors = new int[126];
            colors[1] = 0x010203;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, colors));
            var outs = io.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();
            Assert.Equal(8, outs.Count);
            Assert.Equal("04 26 00 06 08 00 00 00 14 04 00 00 00 00 00 00", Hex(outs[0].Take(16)));
            // Command 11, 54 bytes: 11 + 36 + 01 + 02 + 03 = 0x4D.
            Assert.Equal("04 4D 00 11 36 00 00 00 00 00 00 01 02 03", Hex(outs[1].Take(14)));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(16, io.Calls.Count);
        }

        // ── The second generation ──

        /// <summary>A keyboard that answers each query as Query expects
        /// (EVisionV2KeyboardController.cpp:140-177): the report it was sent,
        /// status 0 in byte 7, and for the capabilities AA 55, then the
        /// frame's size in keys at byte 5.</summary>
        private static FakeReportChannel Keyboard(byte mapSize = 126, byte status = 0, bool wrongEcho = false)
        {
            var io = new FakeReportChannel { OutputReportLength = 64, InputReportLength = 64 };
            io.Input = () =>
            {
                var sent = io.Calls.Last(c => c.Kind == "out").Bytes;
                var answer = (byte[])sent.Clone();
                answer[7] = status;
                if (wrongEcho) answer[3] ^= 0xFF;
                if (sent[3] == 0x03)
                {
                    var capabilities = new byte[] { 0xAA, 0x55, 0x01, 0x02, 0x03, mapSize, 0x04 };
                    capabilities.CopyTo(answer, 8);
                }
                return answer;
            };
            return io;
        }

        [Fact]
        public void TheSecondGeneration_ReadsItsCapabilities_AndSendsDynamicColors()
        {
            var io = Keyboard();
            var model = ModelNamed("EVision Keyboard 320F:5041");
            var session = Assert.IsType<EVisionKeyboardLighting.V2Session>(model.Begin(io, _ => { }));
            Assert.Equal(126, session.MapSize);
            // Read(03, 0, 7): 03 + 07 = 0x0A.
            Assert.Equal("04 0A 00 03 07 00 00 00", Hex(io.Calls.First().Bytes.Take(8)));
            Assert.Empty(session.CrashFrames);

            var colors = new int[106];
            colors[0] = 0x0A0B0C;
            colors[1] = 0x0D0E0F;
            int before = io.Calls.Count;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, colors));
            var outs = io.Calls.Skip(before).Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();
            // 126 keys of three bytes: six queries of 56 and one of 42.
            Assert.Equal(7, outs.Count);
            Assert.Equal("12 38 00 00 00 0A 0B 0C", Hex(outs[0].Skip(3).Take(8)));
            // LED 1's key value is 12 (EVisionV2KeyboardController.cpp:32): byte 36 of the frame.
            Assert.Equal("0D 0E 0F", Hex(outs[0].Skip(8 + 36).Take(3)));
            Assert.Equal("12 38 38 00", Hex(outs[1].Skip(3).Take(4)));
            Assert.Equal("12 2A 50 01", Hex(outs[6].Skip(3).Take(4)));
            // EndLedsDirect, 13: 13 = 0x13.
            Assert.Equal("04 13 00 13 00 00 00 00", Hex(Assert.Single(session.CrashFrames).Take(8)));

            // The keep-alive, RefreshLedDirect: one byte at 18, 12 + 01 + 12 = 0x25.
            before = io.Calls.Count;
            Assert.True(session.KeepAlive(io, _ => { }, model.Map.Lights, colors));
            var refresh = io.Calls.Skip(before).Where(c => c.Kind == "out").ToList();
            Assert.Equal("04 25 00 12 01 12 00 00 00", Hex(Assert.Single(refresh).Bytes.Take(9)));

            before = io.Calls.Count;
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal("04 13 00 13 00 00 00 00", Hex(io.Calls.Skip(before).First().Bytes.Take(8)));
            Assert.Empty(session.CrashFrames);
        }

        /// <summary>Endorfy's key values (EVisionV2KeyboardController.cpp:40-49):
        /// its LED 1 sits at key 2.</summary>
        [Fact]
        public void TheEndorfy_UsesItsOwnKeyValues()
        {
            var io = Keyboard();
            var model = ModelNamed("Endorfy Omnis");
            var session = model.Begin(io, _ => { });
            var colors = new int[104];
            colors[1] = 0x0D0E0F;
            session.Paint(io, _ => { }, model.Map.Lights, colors);
            var first = io.Calls.Where(c => c.Kind == "out").Skip(1).First().Bytes;
            Assert.Equal("0D 0E 0F", Hex(first.Skip(8 + 6).Take(3)));
        }

        [Fact]
        public void AQueryAnsweredWrong_Fails()
        {
            var model = ModelNamed("EVision Keyboard 320F:5041");
            Assert.Null(model.Begin(Keyboard(status: 1), _ => { }));
            Assert.Null(model.Begin(Keyboard(wrongEcho: true), _ => { }));
            var silent = new FakeReportChannel { OutputReportLength = 64 };
            Assert.Null(model.Begin(silent, _ => { }));
            // OpenRGB's capability check passes when either byte matches (lines 66-69).
            var io = Keyboard();
            var inner = io.Input;
            io.Input = () =>
            {
                var answer = inner();
                if (answer[3] == 0x03) answer[8] = 0x00;
                return answer;
            };
            Assert.NotNull(model.Begin(io, _ => { }));
        }
    }
}
