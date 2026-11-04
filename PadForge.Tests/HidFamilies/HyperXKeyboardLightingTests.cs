using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The HyperX keyboard family against OpenRGB's
    /// HyperXKeyboardController at the pinned commit (#557). Expected frames
    /// are written out here from the reference.</summary>
    public class HyperXKeyboardLightingTests
    {
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');
        private static void Same(byte[] expected, byte[] actual) => Assert.Equal(Hex(expected), Hex(actual));

        private static byte[] R(int length, params (int At, byte Value)[] bytes)
        {
            var frame = new byte[length];
            foreach (var (at, value) in bytes) frame[at] = value;
            return frame;
        }

        private static HidCollectionInfo Collection(OpenRgbRegistration r) => new()
        {
            Path = $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}&mi_{r.Interface:x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            VendorId = (ushort)r.VendorId,
            ProductId = (ushort)r.ProductId,
            UsagePage = r.UsagePage == OpenRgbRegistration.Any ? (ushort)0xFF00 : (ushort)r.UsagePage,
            Usage = 1,
            InterfaceNumber = r.Interface,
        };

        private static HidLightingModel ModelOf(int vid, int pid)
            => HyperXKeyboardLighting.Instance.Match(Collection(
                OpenRgbRegistrations.Of(HyperXKeyboardLighting.OpenRgbFamily).First(r => r.VendorId == vid && r.ProductId == pid)));

        private static readonly Action<int> NoWait = _ => { };

        [Fact]
        public void EveryWindowsRegistration_IsMatched()
        {
            var registrations = OpenRgbRegistrations.Of(HyperXKeyboardLighting.OpenRgbFamily).ToList();
            // The _WIN32 branches of the eight *ControllerDetect.cpp files.
            Assert.Equal(13, registrations.Count);
            foreach (var r in registrations)
            {
                var model = HyperXKeyboardLighting.Instance.Match(Collection(r));
                Assert.True(model != null, r.Name);
                Assert.Equal(r.Name, model.Name);
                Assert.Equal(new[] { VendorPrograms.Ngenuity }, model.Programs);
                Assert.Same(model, HidLightingFamilies.Match(Collection(r)));
                // RGBController_HyperXAlloyElite2.cpp:308-323 (1000 ms), and 50 ms
                // in the family's other RGBControllers, a resend timed as a picture.
                int keepAlive = r.Detector == "DetectHyperXAlloyElite2" ? 1000 : 50;
                Assert.Equal(keepAlive, model.KeepAliveMs);
                Assert.Equal(keepAlive, model.KeepAliveIdleMs);
                Assert.Equal(0, model.RecheckMs);
            }
            // Another interface is not a lighting collection.
            var elite2 = registrations.First(r => r.ProductId == 0x1711);
            Assert.Null(HyperXKeyboardLighting.Instance.Match(Collection(elite2) with
            {
                InterfaceNumber = 0, Path = Collection(elite2).Path.Replace("mi_03", "mi_00"),
            }));
            Assert.True(VendorPrograms.Ngenuity.Owns("NGenuity"));
            Assert.True(VendorPrograms.Ngenuity.Owns("NGenuity2Helper"));
            Assert.Contains(VendorPrograms.Ngenuity, VendorPrograms.All);
        }

        [Fact]
        public void TheLights_AreOpenRgbsLedNamesInOrder()
        {
            Assert.Equal(128, ModelOf(0x0951, 0x1711).Map.Lights.Count);
            Assert.Equal(128, ModelOf(0x0951, 0x16BE).Map.Lights.Count);
            Assert.Equal(106, ModelOf(0x0951, 0x16DC).Map.Lights.Count);
            Assert.Equal(107, ModelOf(0x0951, 0x16E5).Map.Lights.Count);
            Assert.Equal(71, ModelOf(0x0951, 0x1734).Map.Lights.Count);
            Assert.Equal(71, ModelOf(0x03F0, 0x0C8E).Map.Lights.Count);
            Assert.Equal(77, ModelOf(0x03F0, 0x038F).Map.Lights.Count);
            Assert.Equal(74, ModelOf(0x03F0, 0x0CC2).Map.Lights.Count);
            Assert.Equal(10, ModelOf(0x03F0, 0x08C2).Map.Lights.Count);
            var elite2 = ModelOf(0x0951, 0x1711).Map.Lights;
            // KEY_EN_ESCAPE first, KEY_EN_BACK_TICK second, the 18 strip LEDs
            // last (RGBController_HyperXAlloyElite2.cpp:51-200).
            Assert.Equal(0x29, elite2[0].Usage);
            Assert.Equal(0x35, elite2[1].Usage);
            Assert.All(elite2.Skip(110), l => Assert.Equal(LightKind.Edge, l.Kind));
            Assert.Equal(Enumerable.Range(0, 128), elite2.Select(l => l.Address));
            Assert.Equal(LightLabel.Mute, elite2.First(l => l.Kind == LightKind.Other).Label);
            // Every name of every table is a key, an edge, a label, or one of
            // the names no table of OpenRGB's maps (an empty slot, a split
            // space bar, the Ducky's calculator and volume keys).
            var unnamed = new HashSet<string> { "", "Left Space", "Right Space", "Key: / (ABNT)", "Key: Calculator", "Key: Media Volume +", "Key: Media Volume -" };
            foreach (var table in new[]
            {
                OpenRgbLedNames.HyperXAlloyElite2, OpenRgbLedNames.HyperXAlloyElite, OpenRgbLedNames.HyperXAlloyFps,
                OpenRgbLedNames.HyperXAlloyOrigins, OpenRgbLedNames.HyperXAlloyOrigins60, OpenRgbLedNames.HyperXAlloyOrigins65,
                OpenRgbLedNames.HyperXOrigins2_65, OpenRgbLedNames.DuckyKeyboard, OpenRgbLedNames.CougarKeyboard,
            })
                foreach (string name in table)
                    Assert.True(name.StartsWith("RGB Strip", StringComparison.Ordinal) || unnamed.Contains(name)
                                || OpenRgbKeys.TryGet(name, out _, out _), name);
        }

        [Fact]
        public void TheLedNameTables_AreTheGeneratorsAtThePin()
        {
            Assert.Equal(KeyboardLayouts.OpenRgbCommit, OpenRgbLedNames.OpenRgbCommit);
        }

        [Fact]
        public void TheElite2_SendsStartThenSixteenEntriesAReport_WithBlanksAtTheSkips()
        {
            var colors = Enumerable.Range(1, 128).Select(i => i << 16 | i << 8 | i).ToArray();
            var frames = HyperXKeyboardLighting.Elite2Frames(colors);
            // 04 F2, then 144 entries (128 colors, 16 skips) in nine reports.
            Assert.Equal(10, frames.Count);
            Same(R(65, (1, 0x04), (2, 0xF2)), frames[0]);
            // Entry 23 is the first skip: 0x81 00 00 00 (HyperXAlloyElite2Controller.cpp:19, 97-111).
            var second = frames[2];
            Assert.Equal(0x81, second[1 + 4 * 7]);
            Assert.Equal(0, second[2 + 4 * 7]);
            Assert.Equal(23, second[2 + 4 * 6]); // color 23 sits just before the skip
            Assert.Equal(24, second[2 + 4 * 8]);
            // The first report: colors 1 to 16 as 81 RR GG BB.
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(0x81, frames[1][1 + 4 * i]);
                Assert.Equal(i + 1, frames[1][2 + 4 * i]);
            }
            // A short picture is padded to nine reports with empty ones.
            frames = HyperXKeyboardLighting.Elite2Frames(new[] { 0x010203 });
            Assert.Equal(10, frames.Count);
            Same(R(65, (1, 0x81), (2, 1), (3, 2), (4, 3)), frames[1]);
            Assert.All(frames.Skip(2), f => Same(new byte[65], f));
        }

        [Fact]
        public void TheOrigins_InsertsBlanksAtItsSkips_AndSendsNineReports()
        {
            var colors = Enumerable.Range(1, 107).Select(i => i).ToArray();
            var frames = HyperXKeyboardLighting.OriginsFrames(colors, 9, HyperXKeyboardLighting.OriginsSkip);
            // 04 F2 with 09 at byte 9 (HyperXAlloyOriginsController.cpp:95-110).
            Same(R(65, (1, 0x04), (2, 0xF2), (9, 0x09)), frames[0]);
            Assert.Equal(10, frames.Count);
            // 107 + 19 blanks = 126 entries: seven full reports, one of 14, one
            // with a single black entry.
            Assert.Equal(0x81, frames[8][1 + 4 * 13]);
            Assert.Equal(0, frames[8][1 + 4 * 14]);
            Same(R(65, (1, 0x81)), frames[9]);
            // Position 23 holds a blank, and color 24 (the 24th) moves to 24.
            Assert.Equal(0, frames[2][4 + 4 * 7]);
            Assert.Equal(23, frames[2][4 + 4 * 6]);
            Assert.Equal(24, frames[2][4 + 4 * 8]);
            // The 60 and 65: five reports, 05 at byte 9, no blanks.
            frames = HyperXKeyboardLighting.OriginsFrames(Enumerable.Range(1, 71).ToArray(), 5, null);
            Assert.Equal(6, frames.Count);
            Same(R(65, (1, 0x04), (2, 0xF2), (9, 0x05)), frames[0]);
            Assert.Equal(65, frames[5][4]);
            Assert.Equal(71, frames[5][4 + 4 * 6]);
            Assert.Equal(0, frames[5][1 + 4 * 7]);
        }

        [Fact]
        public void TheEliteAndFps_SendAReportPerChannel_WithKeysAtTheirBytes()
        {
            var colors = new int[128];
            colors[0] = 0x112233;
            colors[105] = 0x445566;
            colors[106] = 0x778899;
            var red = HyperXKeyboardLighting.ChannelReport(0x01, colors, 16);
            // 07 16 01 A0, key 0 at 0x08, key 105 at 0x95 (HyperXAlloyEliteController.cpp:17-25, 442-475).
            Same(R(264, (0, 0x07), (1, 0x16), (2, 0x01), (3, 0xA0), (0x08, 0x11), (0x95, 0x44)), red);
            var extended = HyperXKeyboardLighting.ExtendedReport(colors);
            // LED 106 at 0x08 red, 0x29 green, 0x39 blue (lines 27-29).
            Same(R(264, (0, 0x07), (1, 0x16), (2, 0x04), (3, 0xA0), (0x08, 0x77), (0x29, 0x88), (0x39, 0x99)), extended);

            var session = (HyperXKeyboardLighting.Session)ModelOf(0x0951, 0x16DC).Begin(new FakeReportChannel(), NoWait);
            Assert.True(session.Feature);
            Assert.Equal(10, session.GapMs);
            var lights = ModelOf(0x0951, 0x16DC).Map.Lights;
            Assert.Equal(3, session.PictureFrames(lights, new int[lights.Count]).Count);
            var elite = (HyperXKeyboardLighting.Session)ModelOf(0x0951, 0x16BE).Begin(new FakeReportChannel(), NoWait);
            Assert.Equal(5, elite.GapMs);
            lights = ModelOf(0x0951, 0x16BE).Map.Lights;
            Assert.Equal(4, elite.PictureFrames(lights, new int[lights.Count]).Count);
        }

        [Fact]
        public void TheCore_ReadsItsVariant_AndSendsItsBufferInSixtyByteReports()
        {
            var channel = new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65 };
            var answer = new byte[65];
            answer[57] = 0x10; // byte 56 after hidapi drops report ID 0
            channel.Input = () => answer;
            var model = ModelOf(0x0951, 0x16E6);
            var session = (HyperXKeyboardLighting.Session)model.Begin(channel, NoWait);
            Assert.NotNull(session);
            Same(R(65, (1, 0x10)), channel.Calls.First(c => c.Kind == "out").Bytes);
            Assert.NotNull(session.Map); // ABNT2
            Assert.False(session.Feature);

            var lights = model.Map.Lights;
            var colors = new int[lights.Count];
            int escape = Enumerable.Range(0, lights.Count).First(i => lights[i].Usage == 0x29);
            colors[escape] = 0x112233;
            var frames = HyperXKeyboardLighting.CoreFrames(lights, colors);
            // 380 bytes in reports of 60: six, then one of 20 (SendRGBToDevice).
            Assert.Equal(7, frames.Count);
            Assert.Equal(new byte[] { 0x00, 0xA2, 0x06, 0x00, 20 }, frames[6].Take(5).ToArray());
            int value = lights[escape].Address;
            int at = value / 12 * 48 + (value / 6 & 1) * 8 + value % 6;
            Assert.Equal(0x22, frames[at / 60][5 + at % 60]);
            Assert.Equal(0x11, frames[(at + 16) / 60][5 + (at + 16) % 60]);
            Assert.Equal(0x33, frames[(at + 32) / 60][5 + (at + 32) % 60]);
            Assert.Equal(60, frames[0][4]);

            // No answer: no session.
            channel.Input = () => null;
            Assert.Null(model.Begin(channel, NoWait));
            // Any other variant is ANSI.
            Assert.Equal(KeyboardLayout.AnsiQwerty, HyperXKeyboardLighting.CoreRegionOf(new byte[64]));
        }

        [Fact]
        public void TheEveAndOrigins2_65_SendReport44Frames()
        {
            // Both write output reports (hid_write).
            foreach (int pid in new[] { 0x08C2, 0x0CC2 })
            {
                var session = (HyperXKeyboardLighting.Session)ModelOf(0x03F0, pid).Begin(new FakeReportChannel(), NoWait);
                Assert.False(session.Feature);
            }
            var colors = Enumerable.Range(1, 74).ToArray();
            var frames = HyperXKeyboardLighting.Report44Frames(colors, 4, true);
            // 44 01 04 00, then 44 02 nn 00 and twenty colors
            // (HyperXOrigins2_65Controller.cpp:51-151).
            Same(R(65, (0, 0x44), (1, 0x01), (2, 0x04)), frames[0]);
            Assert.Equal(5, frames.Count);
            Assert.Equal(new byte[] { 0x44, 0x02, 0x03, 0x00 }, frames[4].Take(4).ToArray());
            Assert.Equal(61, frames[4][6]); // color 61, the first of the last report
            Assert.Equal(74, frames[4][6 + 3 * 13]);
            Assert.Equal(0, frames[4][6 + 3 * 14]);
            // The Eve: one report, numbered 0.
            frames = HyperXKeyboardLighting.Report44Frames(Enumerable.Range(1, 10).ToArray(), 1, false);
            Assert.Equal(2, frames.Count);
            Assert.Equal(new byte[] { 0x44, 0x02, 0x00, 0x00, 0x00, 0x00, 0x01 }, frames[1].Take(7).ToArray());
            // A report with no colors left carries one black color.
            frames = HyperXKeyboardLighting.Report44Frames(new int[20], 2, true);
            Same(R(65, (0, 0x44), (1, 0x02), (2, 0x01)), frames[2]);
        }

        [Fact]
        public void ASession_SendsItsLastPictureAgainOnTheKeepAlive_AndHandsBackNothing()
        {
            var channel = new FakeReportChannel { FeatureReportLength = 65 };
            var model = ModelOf(0x0951, 0x1734);
            var session = model.Begin(channel, NoWait);
            Assert.Empty(session.CrashFrames);
            var lights = model.Map.Lights;
            var colors = Enumerable.Range(1, lights.Count).Select(i => i * 0x010101).ToArray();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(channel, NoWait, lights, colors));
            var picture = channel.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).ToList();
            Assert.Equal(6, picture.Count);
            // The keep-alive is the same picture, report for report.
            Assert.True(session.KeepAlive(channel, NoWait, lights, colors));
            var again = channel.Calls.Where(c => c.Kind == "set").Skip(6).Select(c => c.Bytes).ToList();
            Assert.Equal(picture, again);
            Assert.True(session.HandBack(channel, NoWait));
            Assert.Equal(12, channel.Calls.Count);
            channel.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(channel, NoWait, lights, new int[lights.Count]));
        }

        [Fact]
        public void TheEliteSessions_WaitBetweenTheirReports()
        {
            var waits = new List<int>();
            var channel = new FakeReportChannel { FeatureReportLength = 264 };
            var model = ModelOf(0x0951, 0x16BE);
            var session = model.Begin(channel, NoWait);
            session.Paint(channel, ms => waits.Add(ms), model.Map.Lights, new int[model.Map.Lights.Count]);
            Assert.Equal(new[] { 5, 5, 5 }, waits);
        }
    }
}
