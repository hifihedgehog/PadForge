using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>EVGA's keyboards and mice against OpenRGB's EVGAUSBController
    /// (#557): SetLedsDirect and SetHWModes (EVGAKeyboardController.cpp:103-206),
    /// the X20's RefreshDeviceState, SetMode and SetLed
    /// (EVGAMouseController.cpp:112-343), and the X12's GetReport,
    /// SetStaticReport and SetModeReport (EVGAX12Controller.cpp:71-268).</summary>
    public class EvgaLightingTests
    {
        private static OpenRgbRegistration Reg(ushort pid)
            => OpenRgbRegistrations.Of(EvgaLighting.OpenRgbFamily).Single(r => r.ProductId == pid);

        private static ushort LengthOf(OpenRgbRegistration r) => r.Detector switch
        {
            EvgaLighting.KeyboardDetector => 792,
            EvgaLighting.X12Detector => 40,
            _ => 65,
        };

        private static HidLightingModel ModelOf(ushort pid)
            => EvgaLighting.Instance.Match(CollectionOf(Reg(pid), feature: LengthOf(Reg(pid))));

        [Fact]
        public void EveryRegistration_FindsItsModel()
        {
            var registrations = OpenRgbRegistrations.Of(EvgaLighting.OpenRgbFamily).ToList();
            Assert.Equal(7, registrations.Count);
            foreach (var r in registrations)
            {
                var model = EvgaLighting.Instance.Match(CollectionOf(r, feature: LengthOf(r)));
                Assert.NotNull(model);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(CollectionOf(r, feature: LengthOf(r))));
                Assert.Null(EvgaLighting.Instance.Match(CollectionOf(r, feature: (ushort)(LengthOf(r) - 1))));
                Assert.Empty(model.Programs);
            }
            Assert.Equal(108, ModelOf(0x2608).Map.Lights.Count);
            Assert.Equal(108, ModelOf(0x260E).Map.Lights.Count);
            Assert.Equal(132, ModelOf(0x260A).Map.Lights.Count);
            Assert.Equal(132, ModelOf(0x2610).Map.Lights.Count);
            Assert.Equal(3, ModelOf(0x2420).Map.Lights.Count);
            Assert.Equal(150, ModelOf(0x2420).HoldMs);
            Assert.Equal(500, ModelOf(0x2402).RepaintMs);
            Assert.Equal(50, ModelOf(0x2422).RepaintMs);
            Assert.Equal(0, ModelOf(0x2608).HoldMs);
        }

        [Fact]
        public void TheKeyboardLights_AreOpenRgbsTable()
        {
            var lights = EvgaLighting.KeyboardLights(z20: true);
            Assert.Equal(Enumerable.Range(0, 132), lights.Select(l => l.Address));
            Assert.Equal((ushort)0x29, lights[0].Usage);
            Assert.Equal((0f, 0f), (lights[0].X, lights[0].Y));
            Assert.All(lights, l => Assert.True(l.HasPosition));
            Assert.Equal(18, lights.Count(l => l.Kind == LightKind.Edge));
            Assert.All(lights.Skip(108).Take(18), l => Assert.Equal(LightKind.Edge, l.Kind));
            // The feature and macro keys are keys no source names.
            Assert.All(lights.Skip(126), l => Assert.Equal((LightKind.Key, (ushort)0), (l.Kind, l.Usage)));
            Assert.Equal(LightLabel.PlayPause, lights[105].Label);
            // The right side lights sit in a row of their own below the keys.
            Assert.Equal((0f, 6f), (lights[108].X, lights[108].Y));
            Assert.Equal((5f, 8f), (lights[131].X, lights[131].Y));
        }

        /// <summary>06 EA 02 01, the checksum 0 less bytes 8 to 791, and
        /// each LED's FF R G B at 8 + 4 x its packet map slot: Escape's slot
        /// 1 (EVGAKeyboardController.cpp:15-58).</summary>
        [Fact]
        public void TheKeyboardReport_IsSetLedsDirect()
        {
            var lights = EvgaLighting.KeyboardLights(z20: false);
            var colors = lights.Select(l => l.Address == 0 ? 0x112233 : 0).ToList();
            var report = EvgaLighting.DirectFrame(lights, colors);
            Assert.Equal(792, report.Length);
            Assert.Equal("06 EA 02 01 00 00 00", Hex(report, 7));
            // Every other LED is at full brightness and black.
            int expectedSum = 0xFF * 108 + 0x11 + 0x22 + 0x33;
            Assert.Equal(expectedSum, report.Skip(8).Sum(b => b));
            Assert.Equal((byte)(-expectedSum), report[7]);
            Assert.Equal("FF 11 22 33", Hex(report.Skip(12).Take(4).ToArray()));
            Assert.Equal(Padded("06 EA 02", 792), EvgaLighting.HardwareModesFrame());
        }

        [Fact]
        public void TheKeyboard_HandsBackToItsHardwareModes()
        {
            var lights = EvgaLighting.KeyboardLights(z20: false);
            var io = new LoggingHidChannel();
            var session = new EvgaLighting.KeyboardSession();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(io.Log);
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new int[108]));
            Assert.Equal(new[] { EvgaLighting.HardwareModesFrame() }, session.CrashFrames);
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(EvgaLighting.HardwareModesFrame(), io.Log[^1].Bytes);
            Assert.Empty(session.CrashFrames);
            var failing = new LoggingHidChannel { FailWrites = true };
            Assert.Equal(HidLightingPaint.Failed, session.Paint(failing, io.Wait, lights, new int[108]));
            Assert.NotEmpty(session.CrashFrames);
            Assert.False(session.HandBack(failing, io.Wait));
        }

        // ── The X20 ──

        /// <summary>An X20 that answers each light's state with the given
        /// mode, brightness, speed and two colors, and every write with a
        /// plain answer.</summary>
        private static LoggingHidChannel X20(byte mode = 2, int notReady = 0)
        {
            var io = new LoggingHidChannel();
            for (int led = 0; led < 3; led++)
            {
                for (int i = 0; i < notReady; i++)
                    io.Answers.Enqueue(Padded("00 A0 00 02 1D 02 80 02", 65));
                var answer = Padded("00 00 00 00 1D 02 80 02", 65);
                answer[8] = (byte)led;
                answer[9] = mode;
                answer[10] = (byte)(0x40 + led);
                answer[11] = 0x33;
                answer[14] = 2;
                answer[15] = (byte)(0x10 * (led + 1));
                answer[18] = 0xAB;
                io.Answers.Enqueue(answer);
            }
            io.Echo = _ => new byte[65];
            return io;
        }

        [Fact]
        public void TheX20Reports_AreOpenRgbs()
        {
            Assert.Equal(Padded("00 00 00 02 1D 02 80 02 01", 65), EvgaLighting.MouseReadFrame(2, 1));
            Assert.Equal(Padded("00 00 00 00 1D 02 81 01 02 01", 65), EvgaLighting.MouseModeFrame(2, 1));
            Assert.Equal(Padded("00 00 00 00 1D 02 00 02 00 01 64 00 00 00 01 11 22 33", 65),
                EvgaLighting.MouseColorFrame(0, 0, 1, 100, 0, new[] { 0x112233 }));
            Assert.Equal(Padded("00 A1 00 02 1D 02 00 02 01 02 40 33 00 00 02 11 22 33 44 55 66", 65),
                EvgaLighting.MouseColorFrame(2, 1, 2, 0x40, 0x33, new[] { 0x112233, 0x445566 }, activate: true));
            Assert.True(EvgaLighting.NotReady(Padded("00 A0 00 02 1D 02 80 02", 65)));
            Assert.True(EvgaLighting.Asleep(Padded("00 A4 00 02 1D 02 80 02", 65)));
            Assert.False(EvgaLighting.Asleep(Padded("00 A0 00 02 1D 02 80 02", 65)));
        }

        [Fact]
        public void TheX20_ReadsEachLight_ThenSetsStaticOnce_ThenColors()
        {
            var io = X20(notReady: 1);
            var session = (EvgaLighting.MouseSession)EvgaLighting.MouseSession.Begin(io, io.Wait, 2);
            Assert.NotNull(session);
            Assert.Equal(new[] { 2, 2, 2 }, session.Saved.Select(s => (int)s.Mode));
            Assert.Equal(new[] { 0x40, 0x41, 0x42 }, session.Saved.Select(s => (int)s.Brightness));
            Assert.Equal(new[] { 0x100000, 0xAB0000 }, session.Saved[0].Colors);
            Assert.Equal(0x33, session.Saved[2].Speed);
            // Per light: the request, two waits, a "not ready" read, a wait and
            // the answer.
            Assert.Equal(new[] { "F 00 00 00 02 1D 02 80 02 00", "W 10", "W 10", "G 00", "W 10", "G 00" }, io.Lines(9).Take(6));
            io.Log.Clear();

            var lights = EvgaLighting.MouseLights();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new[] { 0x112233, 0x445566, 0x778899 }));
            var lines = io.Lines(18);
            Assert.Equal("F 00 00 00 00 1D 02 81 01 00 01 00 00 00 00 00 00 00 00", lines[0]);
            Assert.Equal("G 00", lines[1]);
            Assert.Equal("F 00 00 00 02 1D 02 00 02 00 01 64 00 00 00 01 11 22 33", lines[6]);
            Assert.Equal(new[] { "W 10", "G 00" }, lines.Skip(7).Take(2));
            Assert.Equal(6 + 3 * 3, lines.Count);
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new[] { 0, 0, 0 }));
            Assert.Equal(9, io.Log.Count);
        }

        [Fact]
        public void TheX20_HandsBackWhatItRead()
        {
            var io = X20(mode: 2);
            var session = (EvgaLighting.MouseSession)EvgaLighting.MouseSession.Begin(io, io.Wait, 0);
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, EvgaLighting.MouseLights(), new int[3]));
            var restore = session.RestoreFrames();
            Assert.Equal(restore, session.CrashFrames);
            // Each light's mode, then its colors twice, the second to
            // activate breathing.
            Assert.Equal(3 + 6, restore.Count);
            Assert.Equal(EvgaLighting.MouseModeFrame(1, 2), restore[1]);
            Assert.Equal(EvgaLighting.MouseColorFrame(0, 0, 2, 0x40, 0x33, new[] { 0x100000, 0xAB0000 }), restore[3]);
            Assert.Equal(EvgaLighting.MouseColorFrame(0, 0, 2, 0x40, 0x33, new[] { 0x100000, 0xAB0000 }, activate: true), restore[4]);
            io.Log.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal(restore, io.Sent("F"));
            Assert.Empty(session.CrashFrames);
            // A static mouse is restored without the activating writes.
            var plain = X20(mode: 1);
            var s1 = (EvgaLighting.MouseSession)EvgaLighting.MouseSession.Begin(plain, plain.Wait, 0);
            Assert.Equal(6, s1.RestoreFrames().Count);
        }

        [Fact]
        public void AnX20ThatSleepsOrNeverGetsReady_StartsNoSession()
        {
            var asleep = new LoggingHidChannel();
            asleep.Answers.Enqueue(Padded("00 A4 00 02 1D 02 80 02", 65));
            Assert.Null(EvgaLighting.MouseSession.Begin(asleep, asleep.Wait, 2));
            var never = new LoggingHidChannel { Echo = _ => Padded("00 A0 00 02 1D 02 80 02", 65) };
            Assert.Null(EvgaLighting.MouseSession.Begin(never, never.Wait, 0));
            Assert.Equal(100, never.Log.Count(e => e.Kind == "G"));
            var noColor = new LoggingHidChannel();
            noColor.Answers.Enqueue(Padded("00 00 00 00 1D 02 80 02 00 01", 65));
            Assert.Null(EvgaLighting.MouseSession.Begin(noColor, noColor.Wait, 0));
        }

        // ── The X12 ──

        /// <summary>An X12 that answers a read of each command with what it
        /// holds, and holds what it is written.</summary>
        private sealed class FakeX12
        {
            public readonly byte[] Modes = Padded("07 EA 01 01 01 00 01 01", 40);
            public readonly byte[] Static = Padded("07 EA 01 02 80 10 20 30 00 00 00 00 80 40 50 60 80 70 80 90 01 01 01 01", 40);
            public readonly LoggingHidChannel Io = new();

            public FakeX12()
            {
                Io.Echo = last =>
                {
                    if (last[2] == 0x01) return (byte[])(last[3] == 0x01 ? Modes : Static).Clone();
                    return null;
                };
            }

            public void Take()
            {
                foreach (var (kind, bytes) in Io.Log)
                    if (kind == "F" && bytes[2] == 0x02)
                        Array.Copy(bytes, 4, bytes[3] == 0x01 ? Modes : Static, 4, bytes[3] == 0x01 ? 4 : 20);
            }
        }

        [Fact]
        public void TheX12Reports_AreOpenRgbs()
        {
            Assert.Equal(Padded("07 EA 01 02", 40), EvgaLighting.X12Request(0x02));
            Assert.Equal(Padded("07 EA 02 01 01 00 01 01", 40), EvgaLighting.X12ModeFrame(new byte[] { 1, 0, 1, 1 }));
            Assert.Equal(Padded("07 EA 02 02 FF 11 22 33 00 00 00 00 FF 44 55 66 FF 77 88 99 01 01 01 01", 40),
                EvgaLighting.X12StaticFrame(new (byte, int)[] { (0xFF, 0x112233), (0, 0), (0xFF, 0x445566), (0xFF, 0x778899) }));
        }

        [Fact]
        public void TheX12_ReadsItsState_WritesAndChecks_AndHandsBack()
        {
            var x12 = new FakeX12();
            var io = x12.Io;
            var session = EvgaLighting.X12Session.Begin(io, io.Wait);
            Assert.NotNull(session);
            Assert.Equal(new[] { "F 07 EA 01 01", "W 5", "G 07", "F 07 EA 01 02", "W 5", "G 07" }, io.Lines(4));
            io.Log.Clear();

            var lights = EvgaLighting.X12Lights();
            // The fake holds a write once it is taken, so the check reads it.
            io.Echo = last =>
            {
                x12.Take();
                return last[2] == 0x01 ? (byte[])(last[3] == 0x01 ? x12.Modes : x12.Static).Clone() : null;
            };
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new[] { 0x112233, 0x445566, 0x778899 }));
            var lines = io.Lines(8);
            Assert.Equal("F 07 EA 02 02 FF 11 22 33", lines[0]);
            Assert.Equal(new[] { "W 5", "F 07 EA 01 02 00 00 00 00", "W 5", "G 07" }, lines.Skip(1).Take(4));
            Assert.Equal(new[] { "W 5", "F 07 EA 02 01 01 00 01 01" }, lines.Skip(5));
            Assert.Equal(2, session.CrashFrames.Length);
            Assert.Equal(Padded("07 EA 02 02 80 10 20 30 00 00 00 00 80 40 50 60 80 70 80 90 01 01 01 01", 40), session.CrashFrames[0]);
            Assert.Equal(Padded("07 EA 02 01 01 00 01 01", 40), session.CrashFrames[1]);

            // A later picture writes the colors alone.
            io.Log.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, new int[3]));
            Assert.DoesNotContain(io.Lines(4), l => l == "F 07 EA 02 01");

            io.Log.Clear();
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Contains(io.Sent("F"), f => f.SequenceEqual(Padded("07 EA 02 01 01 00 01 01", 40)));
            Assert.Equal("80 10 20 30", Hex(x12.Static.Skip(4).Take(4).ToArray()));
            Assert.Empty(session.CrashFrames);
        }

        [Fact]
        public void AnX12WriteThatNeverReadsBack_FailsAfterThreeTries()
        {
            var x12 = new FakeX12();
            var session = EvgaLighting.X12Session.Begin(x12.Io, x12.Io.Wait);
            x12.Io.Log.Clear();
            Assert.Equal(HidLightingPaint.Failed, session.Paint(x12.Io, x12.Io.Wait, EvgaLighting.X12Lights(), new[] { 1, 2, 3 }));
            Assert.Equal(3, x12.Io.Sent("F").Count(f => f[2] == 0x02 && f[3] == 0x02));
            Assert.Null(EvgaLighting.X12Session.Begin(new LoggingHidChannel(), _ => { }));
        }
    }
}
