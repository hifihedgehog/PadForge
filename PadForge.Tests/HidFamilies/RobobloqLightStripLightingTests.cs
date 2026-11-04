using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The Robobloq monitor light strip against OpenRGB's
    /// RobobloqLightStripController (#557). The expected bytes were written
    /// from SendPacket, SendMultiPacket and MergeRobobloqRanges
    /// (RobobloqLightStripController.cpp:237-337, RobobloqRangeMerger.cpp:26-155),
    /// step by step, not from the family's builders.</summary>
    public class RobobloqLightStripLightingTests
    {
        /// <summary>The device's answer to 82 from OpenRGB's comment
        /// (RobobloqLightStripController.cpp:377-388), packet ID 02, 34
        /// inches, 71 LEDs, behind report ID 0.</summary>
        private static readonly byte[] Info =
        {
            0x00, 0x52, 0x42, 0x19, 0x02, 0x82, 0x00, 0x05, 0x01, 0x22, 0x01, 0x00, 0x47, 0xcd, 0xab, 0xc5, 0x74,
            0x25, 0xbc, 0xb7, 0xdc, 0xe5, 0x01, 0x08, 0x02,
        };

        /// <summary>71 LEDs: a ramp of red, eleven of one green, a ramp of blue
        /// that wraps at its end.</summary>
        private static int[] Strip()
            => Enumerable.Range(0, 30).Select(i => ((i * 7) & 0xFF) << 16)
                .Concat(Enumerable.Repeat(0x00FF00, 11))
                .Concat(Enumerable.Range(0, 30).Select(i => (i * 9) & 0xFF)).ToArray();

        private const string StripRuns =
            "01 04 00 00 02 03 12 00 00 04 05 20 00 00 06 07 2E 00 00 08 09 3C 00 00 0A 0B 4A 00 00 0C 0D 58 00 00 0E 0F 66 00 00 10 11 74 00 00 12 13 82 00 00 14 15 90 00 00 16 17 9E 00 00 18 19 AC 00 00 1A 1B BA 00 00 1C 1D C8 00 00 1E 1F 00 FF 00 29 2A 00 00 05 2B 2C 00 00 17 2D 2E 00 00 29 2F 30 00 00 3B 31 32 00 00 4D 33 34 00 00 5F 35 36 00 00 71 37 38 00 00 83 39 3A 00 00 95 3B 3C 00 00 A7 3D 3E 00 00 B9 3F 40 00 00 CB 41 42 00 00 D8 42 43 00 00 E1 43 44 00 00 EA 44 45 00 00 F3 45 46 00 00 FC 46 47 00 00 05 47";

        private static HidLightingModel Model()
            => RobobloqLightStripLighting.Instance.Match(CollectionFor(
                OpenRgbRegistrations.Of(RobobloqLightStripLighting.OpenRgbFamily).Single(), output: 65, input: 65));

        [Fact]
        public void ThePackets_AreSendPackets()
        {
            Assert.Equal("00 52 42 06 02 82 1E 00", Hex(RobobloqLightStripLighting.Packet(2, 0x82), 8));
            Assert.Equal("00 52 42 07 03 93 00 31 00", Hex(RobobloqLightStripLighting.Packet(3, 0x93, 0x00), 9));
            Assert.Equal("00 52 42 07 04 87 FF 25 00", Hex(RobobloqLightStripLighting.Packet(4, 0x87, 0xFF), 9));
            Assert.Equal(65, RobobloqLightStripLighting.Packet(2, 0x82).Length);
        }

        [Fact]
        public void TheRuns_AreMergeRobobloqRanges()
        {
            var runs = RobobloqLightStripLighting.MergeRanges(Strip(), 34);
            Assert.Equal(34 * 5, runs.Count);
            Assert.Equal(StripRuns, Hex(runs.ToArray()));
            // Fewer LEDs than runs: each LED its own run.
            Assert.Equal("01 10 20 30 01 02 40 50 60 02", Hex(RobobloqLightStripLighting.MergeRanges(new[] { 0x102030, 0x405060 }, 34).ToArray()));
            // An average rounds half away from zero as std::round does: 2 and
            // 3 make 2.5, sent as 3.
            Assert.Equal("01 03 00 00 02", Hex(RobobloqLightStripLighting.MergeRanges(new[] { 0x020000, 0x030000 }, 1).ToArray()));
        }

        [Fact]
        public void TheModel_HasNoLightsUntilTheDeviceAnswers()
        {
            Assert.Empty(Model().Map.Lights);
        }

        [Fact]
        public void ASession_AsksForTheCount_Opens_AndStreamsTheSyncScreenCommand()
        {
            var model = Model();
            bool answered = false;
            var io = new FakeReportChannel
            {
                OutputReportLength = 65, InputReportLength = 65,
                Input = () => { if (answered) return null; answered = true; return Info; },
            };
            var waits = new List<int>();
            var session = model.Begin(io, Recorder(waits));
            Assert.NotNull(session);
            var map = session.Map;
            Assert.Equal(71, map.Lights.Count);
            // 34 inches: 15 on the right, 41 on top, 15 on the left
            // (RGBController_RobobloqLightStrip.cpp:113-157).
            Assert.Equal(15, map.Lights.Count(l => l.Zone == 1));
            Assert.Equal(41, map.Lights.Count(l => l.Zone == 2));
            Assert.Equal(15, map.Lights.Count(l => l.Zone == 3));
            Assert.Equal(1, map.Lights[14].Zone);
            Assert.Equal(2, map.Lights[15].Zone);
            Assert.Equal(3, map.Lights[56].Zone);
            var calls = Calls(io);
            // 82 at index 2, its answer, then 93 00 and 87 FF, each followed
            // by the read that drops waiting input.
            Assert.Equal(new[] { "out", "in", "out", "in", "out", "in" }, calls.Select(c => c.Kind));
            Assert.Equal("00 52 42 06 02 82 1E", Hex(calls[0].Bytes, 7));
            Assert.Equal("00 52 42 07 03 93 00 31", Hex(calls[2].Bytes, 8));
            Assert.Equal("00 52 42 07 04 87 FF 25", Hex(calls[4].Bytes, 8));
            Assert.Equal(new[] { 10 }, waits);

            io.Calls.Clear();
            waits.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, Recorder(waits), map.Lights, Strip()));
            calls = Calls(io);
            Assert.Equal(new[] { "out", "out", "out" }, calls.Select(c => c.Kind));
            // SendMultiPacket at index 5: 53 43, length 0x00B1, 05, 80, the
            // runs, the sum 8F, padded to three 64-byte packets.
            string all = string.Join(" ", calls.Select(c => Hex(c.Bytes.Skip(1).ToArray())));
            Assert.Equal("53 43 00 B1 05 80 " + StripRuns + " 8F", all.Substring(0, ("53 43 00 B1 05 80 " + StripRuns + " 8F").Length));
            Assert.All(calls, c => Assert.Equal(0, c.Bytes[0]));
            Assert.All(calls, c => Assert.Equal(65, c.Bytes.Length));
            Assert.Equal(new[] { 1, 1, 1 }, waits);
            // The next picture goes out at index 6.
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, map.Lights, Strip()));
            Assert.Equal(0x06, Calls(io)[3].Bytes[5]);
            DeskLightKit.HandsBackNothing(io, session);
        }

        [Fact]
        public void AStripThatDoesNotAnswer_GetsNoSession_AfterThreeTriesWithTheNoOpBetween()
        {
            var model = Model();
            var io = new FakeReportChannel { OutputReportLength = 65, InputReportLength = 65 };
            var waits = new List<int>();
            Assert.Null(model.Begin(io, Recorder(waits)));
            var outs = Calls(io).Where(c => c.Kind == "out").Select(c => Hex(c.Bytes, 8)).ToList();
            // SendPacketWithReply (lines 339-375): the request at index 2,
            // then after each try the no-op at the next index.
            Assert.Equal(new[]
            {
                "00 52 42 06 02 82 1E 00",
                "00 52 42 07 03 93 00 31",
                "00 52 42 07 04 93 00 32",
                "00 52 42 07 05 93 00 33",
            }, outs);
            Assert.Equal(new[] { 10, 10, 10 }, waits);
        }

        [Fact]
        public void AnAnswerForAnotherPacket_IsReadPast()
        {
            var model = Model();
            var stale = (byte[])Info.Clone();
            stale[4] = 0x01;
            var answers = new Queue<byte[]>(new[] { stale, Info });
            var io = new FakeReportChannel
            {
                OutputReportLength = 65, InputReportLength = 65,
                Input = () => answers.Count > 0 ? answers.Dequeue() : null,
            };
            var session = model.Begin(io, _ => { });
            Assert.NotNull(session);
            Assert.Equal(71, session.Map.Lights.Count);
        }

        [Fact]
        public void AnAnswerTooShort_OrWithNoLeds_GetsNoSession()
        {
            foreach (var answer in new[] { Info.Take(24).ToArray(), Info.Select((b, i) => i == 12 ? (byte)0 : b).ToArray() })
            {
                bool answered = false;
                var io = new FakeReportChannel
                {
                    OutputReportLength = 65, InputReportLength = 65,
                    Input = () => { if (answered) return null; answered = true; return answer; },
                };
                Assert.Null(Model().Begin(io, _ => { }));
            }
            // Another screen size lays the LEDs out as one strip.
            var map = RobobloqLightStripLighting.MapOf(60, 27, "p");
            Assert.All(map.Lights, l => Assert.Equal(1, l.Zone));
        }
    }
}
