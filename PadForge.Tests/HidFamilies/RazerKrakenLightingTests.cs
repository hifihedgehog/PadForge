using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The first Kraken headsets against OpenRGB's
    /// RazerKrakenController and openrazer's razerkraken_driver (#557).</summary>
    public class RazerKrakenLightingTests
    {
        /// <summary>A 37-byte request written out: report 04, destination,
        /// length, address high and low, arguments.</summary>
        private static string Req(string head) => head + string.Concat(Enumerable.Repeat(" 00", 37 - head.Split(' ').Length));

        /// <summary>SetModeCustom: the color to 1189 at length 3 on the four
        /// that take it, then the effect byte 01 at the product's address,
        /// 172D on the V2, the Kitty Black Edition V2 and the Ultimate, 1008
        /// on the 7.1 Chroma (RazerKrakenController.cpp:43-63 and 301-328).</summary>
        [Theory]
        [InlineData(0x0510, "17 2D")]
        [InlineData(0x0560, "17 2D")]
        [InlineData(0x0527, "17 2D")]
        [InlineData(0x0504, "10 08")]
        public void APicture_IsTheCustomColorThenTheStaticEffect(int pid, string effect)
        {
            var reports = RazerKrakenLighting.Picture((ushort)pid, 0x123456);
            Assert.Equal(new[] { Req("04 40 03 11 89 12 34 56"), Req("04 40 01 " + effect + " 01") }, reports.Select(r => Hex(r)));
        }

        /// <summary>The Kraken 7.1 takes no color: the effect byte at 1008,
        /// 01 for any color (line 327) and 00, OpenRGB's Off, for black
        /// (lines 330-343).</summary>
        [Theory]
        [InlineData(0x0501)]
        [InlineData(0x0506)]
        public void TheKraken71_TakesTheEffectByteAlone(int pid)
        {
            Assert.Equal(new[] { Req("04 40 01 10 08 01") }, RazerKrakenLighting.Picture((ushort)pid, 0x010000).Select(r => Hex(r)));
            Assert.Equal(new[] { Req("04 40 01 10 08 00") }, RazerKrakenLighting.Picture((ushort)pid, 0x000000).Select(r => Hex(r)));
        }

        private static RazerScriptChannel Headset(Func<byte[], byte[]> answer)
            => new() { AnswerTo = answer, OutputReportLength = 37, InputReportLength = 37 };

        /// <summary>openrazer's reads: 04 00 01 [effect address] and 04 00
        /// 04 1189, each answered by input report 05 with the bytes from
        /// argument 1 (razerkraken_driver.c:111-173). The hand-back writes
        /// the color back at length 4, then the effect byte
        /// (razerkraken_driver.c:354-386).</summary>
        [Fact]
        public void TheHandBack_IsWhatTheReadsGave()
        {
            var io = Headset(r => r[1] != 0x00 ? null
                : r[3] == 0x17 ? new byte[] { 0x05, 0x05 }
                : new byte[] { 0x05, 0xAA, 0xBB, 0xCC, 0x7F });
            var session = RazerKrakenLighting.Session.Begin(io, 0x0510);
            Assert.Equal(new[] { Req("04 00 01 17 2D"), Req("04 00 04 11 89") }, io.Writes.Select(w => Hex(w)));
            Assert.Equal(new[] { Req("04 40 04 11 89 AA BB CC 7F"), Req("04 40 01 17 2D 05") }, session.CrashFrames.Select(f => Hex(f)));
            Assert.False(session.Feature);

            io.Writes.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, RazerKrakenLighting.MapOf(0x0510).Lights, new[] { 0x00FF00 }));
            Assert.Equal(new[] { Req("04 40 03 11 89 00 FF 00"), Req("04 40 01 17 2D 01") }, io.Writes.Select(w => Hex(w)));
            io.Writes.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(new[] { Req("04 40 04 11 89 AA BB CC 7F"), Req("04 40 01 17 2D 05") }, io.Writes.Select(w => Hex(w)));
        }

        /// <summary>The Kraken 7.1's hand-back is its effect byte alone.</summary>
        [Fact]
        public void TheKraken71sHandBack_IsItsEffectByte()
        {
            var io = Headset(r => new byte[] { 0x05, 0x01 });
            var session = RazerKrakenLighting.Session.Begin(io, 0x0501);
            Assert.Equal(new[] { Req("04 00 01 10 08") }, io.Writes.Select(w => Hex(w)));
            Assert.Equal(new[] { Req("04 40 01 10 08 01") }, session.CrashFrames.Select(f => Hex(f)));
        }

        /// <summary>No answer, an answer of another report, or an answer too
        /// short leaves no hand-back, and the headset is still lit.</summary>
        [Fact]
        public void AnUnansweredRead_LeavesNoHandBack()
        {
            foreach (var answer in new Func<byte[], byte[]>[]
                     {
                         _ => null,
                         _ => new byte[] { 0x01, 0x05 },
                         r => r[3] == 0x17 ? new byte[] { 0x05, 0x05 } : new byte[] { 0x05, 0xAA, 0xBB },
                         _ => new byte[] { 0x01, 0x05, 0xAA, 0xBB, 0xCC, 0xDD },
                     })
            {
                var io = Headset(answer);
                var session = RazerKrakenLighting.Session.Begin(io, 0x0510);
                Assert.Empty(session.CrashFrames);
                Assert.True(session.HandBack(io, io.Wait));
                Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, RazerKrakenLighting.MapOf(0x0510).Lights, new[] { 1 }));
            }
        }

        /// <summary>A read sets aside the input reports waiting before it
        /// asks, and waits openrazer's 25 ms for the answer.</summary>
        [Fact]
        public void ARead_DrainsFirstAndWaits25Ms()
        {
            var io = Headset(_ => new byte[] { 0x05, 0x01 });
            RazerKrakenLighting.Read(io, 0x1008, 1);
            Assert.Equal(new[] { "R 0", "O 04 00 01 10 08 00 00 00 00 00 00 00 00 00 00 00", "R 25" }, io.Log);
        }

        [Fact]
        public void AFailedWrite_FailsThePicture()
        {
            var io = new RazerScriptChannel { Fails = _ => true };
            var session = RazerKrakenLighting.Session.Begin(new RazerScriptChannel(), 0x0510);
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, io.Wait, RazerKrakenLighting.MapOf(0x0510).Lights, new[] { 1 }));
        }
    }

    /// <summary>The Kraken V3 headsets against OpenRGB's
    /// RazerKrakenV3Controller (#557).</summary>
    public class RazerKrakenV3LightingTests
    {
        private static string R15(string head) => head + string.Concat(Enumerable.Repeat(" 00", 15 - head.Split(' ').Length));

        /// <summary>The first picture: SetModeDirect, SetBrightness(255), then
        /// SetDirect. The next one is SetDirect alone (RazerKrakenV3Controller.cpp:93-143,
        /// RGBController_RazerKrakenV3.cpp:141-166).</summary>
        [Fact]
        public void TheFirstPicture_EntersDirectModeAtFullBrightness()
        {
            var model = RazerKrakenV3Lighting.Instance.Model("Razer Kraken V3", 0x0549);
            var io = new RazerScriptChannel { OutputReportLength = 15 };
            var session = model.Begin(io, io.Wait);
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, model.Map.Lights, new[] { 0x123456 }));
            Assert.Equal(new[] { R15("40 01 00 0F 08"), R15("40 02 00 00 FF"), R15("40 03 00 12 34 56") }, io.Writes.Select(w => Hex(w)));
            Assert.Equal(new[] { R15("40 01 00 0F 03"), R15("40 02 00 00 FF") }, session.CrashFrames.Select(f => Hex(f)));
            io.Writes.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, model.Map.Lights, new[] { 0xABCDEF }));
            Assert.Equal(new[] { R15("40 03 00 AB CD EF") }, io.Writes.Select(w => Hex(w)));

            // The hand-back is Wave at 255 (cpp:131-158).
            io.Writes.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(new[] { R15("40 01 00 0F 03"), R15("40 02 00 00 FF") }, io.Writes.Select(w => Hex(w)));
            Assert.Empty(session.CrashFrames);
            Assert.False(session.Feature);
        }

        /// <summary>The Kitty V2 Pro's four colors from argument 1.</summary>
        [Fact]
        public void TheKittyV2Pro_SendsFourColors()
        {
            var model = RazerKrakenV3Lighting.Instance.Model("Razer Kraken Kitty V2 Pro", 0x0554);
            var io = new RazerScriptChannel();
            var session = model.Begin(io, io.Wait);
            session.Paint(io, io.Wait, model.Map.Lights, new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C });
            Assert.Equal("40 03 00 01 02 03 04 05 06 07 08 09 0A 0B 0C", Hex(io.Writes.Last()));
        }

        [Fact]
        public void AFailedWrite_FailsAndASecondTryEntersDirectAgain()
        {
            var model = RazerKrakenV3Lighting.Instance.Model("Razer Kraken V3", 0x0549);
            var failing = new RazerScriptChannel { Fails = _ => true };
            var session = model.Begin(failing, failing.Wait);
            Assert.Equal(HidLightingPaint.Failed, session.Paint(failing, failing.Wait, model.Map.Lights, new[] { 1 }));
            // A write that failed may have landed: the hand-back is owed.
            Assert.Equal(2, session.CrashFrames.Length);
            var io = new RazerScriptChannel();
            session.Paint(io, io.Wait, model.Map.Lights, new[] { 1 });
            Assert.Equal(3, io.Writes.Count);
        }
    }

    /// <summary>The Kraken V4 headsets against OpenRGB's
    /// RazerKrakenV4Controller (#557).</summary>
    public class RazerKrakenV4LightingTests
    {
        private static string R64(string head) => head + string.Concat(Enumerable.Repeat(" 00", 64 - head.Split(' ').Length));

        private static string Colors(int count, int from = 1)
            => string.Join(" ", Enumerable.Range(from, count).SelectMany(i => new[] { i, 0x40 + i, 0x80 + i }).Select(b => b.ToString("X2")));

        private static int[] Picture(int count, int from = 1)
            => Enumerable.Range(from, count).Select(i => (i << 16) | ((0x40 + i) << 8) | (0x80 + i)).ToArray();

        /// <summary>SetDirect: report 02, transaction 60, size 5 and three
        /// for each column, class 0F, command 03, the wireless flag, then
        /// 00 00 00 00 and the last column, and the colors. The checksum byte
        /// is left 00 (RazerKrakenV4Controller.cpp:120-136, 178-201 and
        /// 281-284, h:29-43).</summary>
        [Theory]
        [InlineData(0x056B, "00", 9, "20", "08")]
        [InlineData(0x056C, "80", 9, "20", "08")]
        [InlineData(0x0587, "00", 10, "23", "09")]
        [InlineData(0x0588, "80", 10, "23", "09")]
        public void APicture_IsSetDirect(int pid, string wireless, int columns, string size, string last)
        {
            var model = RazerKrakenV4Lighting.Instance.Model("x", (ushort)pid);
            var io = new RazerScriptChannel();
            var session = model.Begin(io, io.Wait);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, model.Map.Lights, Picture(columns)));
            var report = Assert.Single(io.Writes);
            Assert.Equal(64, report.Length);
            Assert.Equal(R64($"02 00 60 00 00 00 {size} 0F 03 {wireless} 00 00 00 00 {last} {Colors(columns)}"), Hex(report));
            Assert.Equal(0, report[62]);
            // Wave at 255 is owed from the first picture on (cpp:138-158).
            Assert.Equal(new[]
            {
                R64($"02 00 60 00 00 00 05 00 00 {wireless} C0 00 01 04"),
                R64($"02 00 60 00 00 00 05 00 00 {wireless} C1 00 01 FF"),
            }, session.CrashFrames.Select(f => Hex(f)));
            Assert.False(session.Feature);
            io.Writes.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(2, io.Writes.Count);
            Assert.Empty(session.CrashFrames);
        }

        [Fact]
        public void ASessionThatPaintedNothing_HandsBackNothing()
        {
            var model = RazerKrakenV4Lighting.Instance.Model("x", 0x056B);
            var io = new RazerScriptChannel();
            var session = model.Begin(io, io.Wait);
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(io.Writes);
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new RazerScriptChannel { Fails = _ => true }, io.Wait, model.Map.Lights, Picture(9)));
        }
    }
}
