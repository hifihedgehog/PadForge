using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.AsusAuraUsbLightingTests;

namespace PadForge.Tests
{
    /// <summary>The ASUS legacy USB family against OpenRGB's
    /// AsusLegacyUSBController at the pinned commit (#557).</summary>
    public class AsusLegacyUsbLightingTests
    {
        private static List<byte[]> Sets(FakeReportChannel io) => io.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).ToList();

        private static List<byte[]> Outs(FakeReportChannel io) => io.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();

        [Fact]
        public void TheFamily_IsOnTheWorkersList()
            => Assert.Contains(AsusLegacyUsbLighting.Instance, HidLightingFamilies.All);

        [Fact]
        public void EveryRegistration_IsMatched_AndWritesRarely()
        {
            var family = new AsusLegacyUsbLighting();
            var rows = OpenRgbRegistrations.Of(AsusLegacyUsbLighting.OpenRgbFamily).ToList();
            Assert.Equal(3, rows.Count);
            foreach (var r in rows)
            {
                var model = family.Match(CollectionOf(r));
                Assert.Equal(r.Name, model.Name);
                Assert.Equal(new[] { VendorPrograms.ArmouryCrate }, model.Programs);
                Assert.Equal(LenovoLegionLighting.HoldMs, model.HoldMs);
                Assert.Equal(LenovoLegionLighting.RepaintMs, model.RepaintMs);
            }
        }

        /// <summary>The Cerberus Mech: SetProfile(1), four 0E frames of
        /// static keys, then the custom mode with direction 1 and brightness
        /// 4 (AsusCerberusKeyboardController.cpp:67-203,
        /// RGBController_AsusCerberusKeyboard.cpp:345-386); a picture is 0D
        /// per key and 0D FF (lines 83-111).</summary>
        [Fact]
        public void TheCerberus_IsOpenedInCustom_AndLitPerKey()
        {
            var opening = AsusLegacyUsbLighting.CerberusOpening();
            Assert.Equal(new[]
            {
                "07 03 01 00 00 00 00 00",
                "07 0E 01 00 00 00 00 00",
                "07 0E 01 01 00 00 00 00",
                "07 0E 01 02 00 00 00 00",
                "07 0E 01 03 00 00 00 00",
                "07 0A 01 07 01 00 00 00",
                "07 0B 01 07 00 00 00 00",
                "07 0C 01 04 00 00 00 00",
            }, opening.Select(Hex));
            var map = AsusLegacyUsbLighting.CerberusMap();
            Assert.Equal(105, map.Lights.Count);
            Assert.Equal(new LightInfo(0x0B, LightKind.Key, 0x29, LightKeySource.Generated) { X = 0, Y = 0, Width = 1, Height = 1, HasPosition = true }, map.Lights[0]);
            Assert.Equal(0, map.UnnamedKeyCount);
            Assert.Equal(AsusAuraTables.CerberusLeds.Select(l => (int)l.Id), map.Lights.Select(l => l.Address));
            var frames = AsusLegacyUsbLighting.CerberusFrames(map.Lights.Take(2).ToList(), new[] { 0x112233, 0x445566 });
            Assert.Equal(new[] { "07 0D 01 0B 11 22 33 00", "07 0D 01 0E 44 55 66 00", "07 0D 01 FF 00 00 00 00" }, frames.Select(Hex));
            var io = Channel();
            var session = new AsusLegacyUsbLighting().Match(CollectionOf(OpenRgbRegistrations.Of(AsusLegacyUsbLighting.OpenRgbFamily)
                .Single(r => r.Name == "ASUS Cerberus Mech"))).Begin(io, _ => { });
            Assert.Equal(8, Sets(io).Count);
            Assert.Empty(session.CrashFrames);
        }

        private static byte[] In(params (int At, byte Value)[] bytes)
        {
            var a = new byte[65];
            a[0] = 0x06;
            foreach (var (at, v) in bytes) a[at] = v;
            return a;
        }

        /// <summary>The Sagaris: GetMode and GetColors first
        /// (AsusSagarisKeyboardController.cpp:64-135), a picture as the
        /// palette's first color divided by 16 and the Static mode at
        /// brightness 3, and the hand-back the palette and the mode it read
        /// (lines 137-185).</summary>
        [Fact]
        public void TheSagaris_ReadsItsState_AndPutsItBack()
        {
            // The keyboard answers each request, and nothing else: the mode
            // once after 0B 03, the seven palette entries after 0B 05.
            var palette = new ConcurrentQueue<byte[]>();
            for (byte i = 0; i < 7; i++) palette.Enqueue(In((4, i), (5, (byte)(i + 1)), (6, (byte)(i + 2))));
            var io = new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65 };
            int seenOuts = 0, sinceOut = 0;
            string lastOut = null;
            io.Input = () =>
            {
                var outsNow = io.Calls.Where(c => c.Kind == "out").ToList();
                if (outsNow.Count != seenOuts)
                {
                    seenOuts = outsNow.Count;
                    lastOut = Hex(outsNow[^1].Bytes.Take(3).ToArray());
                    sinceOut = 0;
                }
                sinceOut++;
                if (lastOut == "06 0B 03" && sinceOut == 1) return In((3, 4), (4, 2), (5, 1), (6, 3));
                if (lastOut == "06 0B 05" && palette.TryDequeue(out var entry)) return entry;
                return null;
            };
            var model = new AsusLegacyUsbLighting().Match(CollectionOf(OpenRgbRegistrations.Of(AsusLegacyUsbLighting.OpenRgbFamily)
                .Single(r => r.Name == "ASUS Sagaris GK1100")));
            var state = AsusLegacyUsbLighting.ReadSagaris(io);
            Assert.NotNull(state);
            var session = new AsusLegacyUsbLighting.SagarisSession(state);
            Assert.Equal((4, 2, 1, 3), ((int)session.State.Mode, (int)session.State.Brightness, (int)session.State.Speed, (int)session.State.ColorIndex));
            Assert.Equal(new byte[] { 6, 7, 8 }, session.State.Palette[6]);
            var outs = Outs(io);
            Starts("06 0B 03", outs[0], 65);
            Starts("06 0B 05", outs[1], 65);

            io.Calls.Clear();
            Assert.Empty(session.CrashFrames);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0xFF8010 }));
            outs = Outs(io);
            Assert.Equal(2, outs.Count);
            Starts("06 0B 04 00 00 0F 08 01", outs[0], 65);
            Starts("06 0B 01 01 03", outs[1], 65);
            Assert.Equal(8, session.CrashFrames.Length);

            io.Calls.Clear();
            Assert.True(session.HandBack(io, _ => { }));
            outs = Outs(io);
            Assert.Equal(8, outs.Count);
            Starts("06 0B 04 00 00 00 01 02", outs[0], 65);
            Starts("06 0B 04 00 06 06 07 08", outs[6], 65);
            Starts("06 0B 01 04 02 01 03", outs[7], 65);
            Assert.Empty(session.CrashFrames);
            Assert.Null(AsusLegacyUsbLighting.ReadSagaris(new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65 }));
            Assert.Null(model.Begin(new FakeReportChannel { InputReportLength = 65, OutputReportLength = 65 }, _ => { }));
        }

        /// <summary>The Strix Claw: the wheel on or off, 07 07 01, and the
        /// logo's brightness, 07 0A 01 00 (AsusStrixClawController.cpp:77-102).</summary>
        [Fact]
        public void TheStrixClaw_TakesOnOffAndBrightness()
        {
            Assert.Equal(new[] { "07 07 01 01 00 00 00 00 00", "07 0A 01 00 C0 00 00 00 00" },
                AsusLegacyUsbLighting.ClawFrames(new[] { 0x000100, 0x40C010 }).Select(Hex));
            Assert.Equal(new[] { "07 07 01 00 00 00 00 00 00", "07 0A 01 00 00 00 00 00 00" },
                AsusLegacyUsbLighting.ClawFrames(new[] { 0, 0 }).Select(Hex));
        }
    }
}
