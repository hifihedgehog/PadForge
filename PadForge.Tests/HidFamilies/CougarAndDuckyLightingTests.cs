using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The Cougar and Ducky families against OpenRGB's
    /// CougarController and DuckyKeyboardController at the pinned commit,
    /// and Aurora's Ducky device (#557). Expected frames are written out
    /// here from the references.</summary>
    public class CougarAndDuckyLightingTests
    {
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');
        private static void Same(byte[] expected, byte[] actual) => Assert.Equal(Hex(expected), Hex(actual));

        private static byte[] R(int length, params byte[] head)
        {
            var frame = new byte[length];
            head.CopyTo(frame, 0);
            return frame;
        }

        private static HidCollectionInfo Collection(OpenRgbRegistration r) => new()
        {
            Path = $@"\\?\hid#vid_{r.VendorId:x4}&pid_{r.ProductId:x4}&mi_{r.Interface:x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
            VendorId = (ushort)r.VendorId,
            ProductId = (ushort)r.ProductId,
            UsagePage = r.UsagePage == OpenRgbRegistration.Any ? (ushort)0xFF00 : (ushort)r.UsagePage,
            Usage = r.Usage == OpenRgbRegistration.Any ? (ushort)1 : (ushort)r.Usage,
            InterfaceNumber = r.Interface,
            OutputReportLength = 65,
        };

        private static HidLightingModel Model(string family, int pid)
            => HidLightingFamilies.Match(Collection(OpenRgbRegistrations.Of(family).First(r => r.ProductId == pid)));

        private static readonly Action<int> NoWait = _ => { };

        [Fact]
        public void EveryRegistration_IsMatched()
        {
            foreach (var family in new[] { CougarLighting.OpenRgbFamily, DuckyKeyboardLighting.OpenRgbFamily })
            {
                var registrations = OpenRgbRegistrations.Of(family).ToList();
                Assert.Equal(2, registrations.Count);
                foreach (var r in registrations)
                {
                    var model = HidLightingFamilies.Match(Collection(r));
                    Assert.True(model != null, r.Name);
                    Assert.Equal(r.Name, model.Name);
                    Assert.Empty(model.Programs);
                }
            }
            Assert.Same(CougarLighting.Instance, Model(CougarLighting.OpenRgbFamily, 0x7010).Family);
            Assert.Same(DuckyKeyboardLighting.Instance, Model(DuckyKeyboardLighting.OpenRgbFamily, 0x0348).Family);
            // The Revenger ST's mouse collection, page 1 usage 2
            // (CougarControllerDetect.cpp:65), opens with no access right only
            // and takes feature reports, so the worker keeps that handle. The
            // 700K EVO's vendor collection (line 64) is an ordinary one.
            Assert.True(Model(CougarLighting.OpenRgbFamily, 0x0412).HeldByWindows);
            Assert.False(Model(CougarLighting.OpenRgbFamily, 0x7010).HeldByWindows);
            // The Revenger ST answers on its mouse collection's interface 0 only.
            var mouse = OpenRgbRegistrations.Of(CougarLighting.OpenRgbFamily).First(r => r.ProductId == 0x0412);
            Assert.Null(CougarLighting.Instance.Match(Collection(mouse) with
            {
                InterfaceNumber = 1, Path = Collection(mouse).Path.Replace("mi_00", "mi_01"),
            }));
        }

        // ── Ducky ──

        [Fact]
        public void TheDuckys_HaveOpenRgbsKeys_TheTklTheFirst108()
        {
            var full = Model(DuckyKeyboardLighting.OpenRgbFamily, 0x0348).Map.Lights;
            var tkl = Model(DuckyKeyboardLighting.OpenRgbFamily, 0x0356).Map.Lights;
            Assert.Equal(132, full.Count);
            Assert.Equal(108, tkl.Count);
            Assert.Equal(full.Take(108), tkl);
            Assert.Equal(0x29, full[0].Usage); // KEY_EN_ESCAPE (RGBController_DuckyKeyboard.cpp:57)
        }

        [Fact]
        public void ADuckySession_TakesOver_PaintsTenFrames_AndReleases()
        {
            var channel = new FakeReportChannel { OutputReportLength = 65 };
            var waits = new List<int>();
            var model = Model(DuckyKeyboardLighting.OpenRgbFamily, 0x0348);
            var session = model.Begin(channel, ms => waits.Add(ms));
            // 41 01 (DuckyKeyboardController.cpp:81-102, Aurora DuckyRGBMappings.cs:16).
            Same(R(65, 0x00, 0x41, 0x01), channel.Calls.Single().Bytes);
            Same(R(65, 0x00, 0x41, 0x00), Assert.Single(session.CrashFrames));

            channel.Calls.Clear();
            var colors = new int[132];
            colors[0] = 0x010203;
            colors[13] = 0x0A0B0C; // bytes 39-41: the first data frame's last, the second's first two
            Assert.Equal(HidLightingPaint.Sent, session.Paint(channel, ms => waits.Add(ms), model.Map.Lights, colors));
            var outs = channel.Calls.Select(c => c.Bytes).ToList();
            Assert.Equal(10, outs.Count);
            // Aurora DuckyStartingPacket (DuckyRGBMappings.cs:13).
            Same(R(65, 0x00, 0x56, 0x81, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0xAA, 0xAA, 0xAA, 0xAA), outs[0]);
            var first = R(65, 0x00, 0x56, 0x83, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x80, 0x01, 0x00, 0xC1, 0x00, 0x00, 0x00, 0x00,
                0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03);
            first[0x19 + 39] = 0x0A;
            Same(first, outs[1]);
            Same(R(65, 0x00, 0x56, 0x83, 0x01, 0x00, 0x0B, 0x0C), outs[2]);
            Same(R(65, 0x00, 0x51, 0x28, 0x00, 0x00, 0xFF), outs[9]);
            Assert.All(waits, w => Assert.Equal(2, w));
            Assert.Equal(11, waits.Count);

            channel.Calls.Clear();
            Assert.True(session.HandBack(channel, NoWait));
            Same(R(65, 0x00, 0x41, 0x00), channel.Calls.Single().Bytes);
            Assert.Empty(session.CrashFrames);
            Assert.True(session.HandBack(channel, NoWait));
            Assert.Single(channel.Calls);
        }

        [Fact]
        public void TheDuckysFrames_CarryOpenRgbs465BytesAndNoMore()
        {
            var colors = Enumerable.Repeat(0xFFFFFF, 160).ToArray();
            var frames = DuckyKeyboardLighting.PictureFrames(colors);
            // 40 + 7 x 60 = 460 of the 465 bytes go out.
            Assert.Equal(0xFF, frames[8][64]);
            Assert.Equal(0xFF, frames[1][64]);
            Assert.Equal(0x00, frames[1][0x18]);
        }

        // ── Cougar 700K EVO ──

        [Fact]
        public void The700k_SendsFourteenKeysAReport_FlaggedFirstMiddleLast()
        {
            var colors = Enumerable.Range(1, 113).ToArray();
            var frames = CougarLighting.KeyboardFrames(colors);
            Assert.Equal(9, frames.Count);
            // 14 2C 0B 00 FF 64 00, flag 1, then ESC (0) and its color
            // (CougarKeyboardController.cpp:18-55 and 158-202).
            Same(R(65, 0x00, 0x14, 0x2C, 0x0B, 0x00, 0xFF, 0x64, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x09, 0x00, 0x00, 0x02),
                frames[0].Take(17).Concat(new byte[48]).ToArray());
            Assert.Equal(2, frames[1][8]);
            Assert.Equal(2, frames[7][8]);
            Assert.Equal(3, frames[8][8]);
            // The last report: key 112 (G5, code 151), then report 7's entries left in the buffer.
            Assert.Equal(151, frames[8][9]);
            Assert.Equal(113, frames[8][12]);
            Assert.Equal(frames[7][13], frames[8][13]);
            // A picture of fewer than 14 is one report flagged 3.
            frames = CougarLighting.KeyboardFrames(new[] { 0x010203 });
            Assert.Equal(3, Assert.Single(frames)[8]);
        }

        [Fact]
        public void The700kSession_WaitsAMillisecondAfterEachReport_AndHandsBackNothing()
        {
            var channel = new FakeReportChannel { OutputReportLength = 65 };
            var waits = new List<int>();
            var model = Model(CougarLighting.OpenRgbFamily, 0x7010);
            Assert.Equal(113, model.Map.Lights.Count);
            var session = model.Begin(channel, NoWait);
            Assert.Empty(channel.Calls);
            Assert.False(session.Feature);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(channel, ms => waits.Add(ms), model.Map.Lights, new int[113]));
            Assert.Equal(9, channel.Calls.Count);
            Assert.Equal(Enumerable.Repeat(1, 9), waits);
            Assert.True(session.HandBack(channel, NoWait));
            Assert.Empty(session.CrashFrames);
        }

        // ── Cougar Revenger ST ──

        [Fact]
        public void TheRevenger_SetsEachZoneDirect_ThenColorsAndBrightnessAtItsAddresses()
        {
            var channel = new FakeReportChannel { FeatureReportLength = 9 };
            var model = Model(CougarLighting.OpenRgbFamily, 0x0412);
            Assert.Equal(3, model.Map.Lights.Count);
            var session = model.Begin(channel, NoWait);
            // ActivateMode(zone, 1) for the three zones (CougarRevengerSTController.cpp:16-25, 186-200).
            var sets = channel.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).ToList();
            Same(new byte[] { 0x00, 0xC4, 0x0F, 0x00, 0x77, 0x01, 0x01, 0x00, 0x00 }, sets[0]);
            Same(new byte[] { 0x00, 0xC4, 0x0F, 0x00, 0x55, 0x00, 0x01, 0x00, 0x00 }, sets[1]);
            Same(new byte[] { 0x00, 0xC4, 0x0F, 0x00, 0xE6, 0x00, 0x01, 0x00, 0x00 }, sets[2]);
            Assert.True(session.Feature);

            channel.Calls.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(channel, NoWait, model.Map.Lights,
                new[] { 0x112233, 0x445566, 0x778899 }));
            sets = channel.Calls.Select(c => c.Bytes).ToList();
            Assert.Equal(12, sets.Count);
            // The bottom: 0x79-0x7B red, green, blue, 0x78 brightness, mode byte 01.
            Same(new byte[] { 0x00, 0xC4, 0x0F, 0x00, 0x79, 0x01, 0x11, 0x00, 0x00 }, sets[0]);
            Same(new byte[] { 0x00, 0xC4, 0x0F, 0x00, 0x7A, 0x01, 0x22, 0x00, 0x00 }, sets[1]);
            Same(new byte[] { 0x00, 0xC4, 0x0F, 0x00, 0x7B, 0x01, 0x33, 0x00, 0x00 }, sets[2]);
            Same(new byte[] { 0x00, 0xC4, 0x0F, 0x00, 0x78, 0x01, 0xFF, 0x00, 0x00 }, sets[3]);
            // The logo: 0xE8 red, mode byte 00.
            Same(new byte[] { 0x00, 0xC4, 0x0F, 0x00, 0xE8, 0x00, 0x77, 0x00, 0x00 }, sets[8]);
            Same(new byte[] { 0x00, 0xC4, 0x0F, 0x00, 0xE7, 0x00, 0xFF, 0x00, 0x00 }, sets[11]);
            Assert.True(session.HandBack(channel, NoWait));
            Assert.Equal(12, channel.Calls.Count);
        }
    }
}
