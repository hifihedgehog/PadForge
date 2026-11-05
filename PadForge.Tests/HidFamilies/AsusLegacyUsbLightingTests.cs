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
        /// RGBController_AsusCerberusKeyboard.cpp:345-386). A picture is 0D
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

    /// <summary>The Sagaris GK1100 against OpenRGB's
    /// AsusSagarisKeyboardController, and its backup in the settings file,
    /// which <see cref="KeyboardProfileBackups"/> keeps for every run.</summary>
    [Collection("PeripheralOutputStatics")]
    public class AsusSagarisTests : IDisposable
    {
        public AsusSagarisTests() => KeyboardProfileBackups.ResetForTest(persistAtOnce: true);

        public void Dispose() => KeyboardProfileBackups.ResetForTest();

        private static List<byte[]> Outs(FakeReportChannel io) => io.Calls.Where(c => c.Kind == "out").Select(c => c.Bytes).ToList();

        private static byte[] In(params (int At, byte Value)[] bytes)
        {
            var a = new byte[65];
            a[0] = 0x06;
            foreach (var (at, v) in bytes) a[at] = v;
            return a;
        }

        /// <summary>A Sagaris that answers each request and nothing else: its
        /// mode once after 0B 03, its seven palette entries after 0B 05, each
        /// entry i as i, i + 1, i + 2.</summary>
        private static FakeReportChannel Keyboard(byte mode, byte brightness, byte speed, byte index, bool answersMode = true)
        {
            var io = new FakeReportChannel { Path = null, InputReportLength = 65, OutputReportLength = 65 };
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
                if (lastOut == "06 0B 03" && sinceOut == 1 && answersMode) return In((3, mode), (4, brightness), (5, speed), (6, index));
                if (lastOut == "06 0B 05" && sinceOut <= 7)
                {
                    byte i = (byte)(sinceOut - 1);
                    return In((4, i), (5, (byte)(i + 1)), (6, (byte)(i + 2)));
                }
                return null;
            };
            return io;
        }

        private static HidLightingModel Model()
            => new AsusLegacyUsbLighting().Match(CollectionOf(OpenRgbRegistrations.Of(AsusLegacyUsbLighting.OpenRgbFamily)
                .Single(r => r.Name == "ASUS Sagaris GK1100")));

        /// <summary>GetMode and GetColors first (AsusSagarisKeyboardController.cpp:64-135),
        /// a picture as the palette's first color divided by 16 and the
        /// Static mode at brightness 3, and the hand-back the palette and the
        /// mode it read (lines 137-185).</summary>
        [Fact]
        public void TheSagaris_ReadsItsState_AndPutsItBack()
        {
            var io = Keyboard(4, 2, 1, 3);
            var model = Model();
            var session = (AsusLegacyUsbLighting.SagarisSession)model.Begin(io, _ => { });
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
            // The backup went into the settings file before the write.
            Assert.Equal(session.State.Contents(), KeyboardProfileBackups.Get(AsusLegacyUsbLighting.SagarisProduct)?.Contents);
            // The same array while it holds, so the crash path's record
            // does not churn (HidLightingSession.CrashFrames).
            var crash = session.CrashFrames;
            session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x000000 });
            Assert.Same(crash, session.CrashFrames);

            io.Calls.Clear();
            Assert.True(session.HandBack(io, _ => { }));
            outs = Outs(io);
            Assert.Equal(8, outs.Count);
            Starts("06 0B 04 00 00 00 01 02", outs[0], 65);
            Starts("06 0B 04 00 06 06 07 08", outs[6], 65);
            Starts("06 0B 01 04 02 01 03", outs[7], 65);
            Assert.Empty(session.CrashFrames);
            Assert.Null(KeyboardProfileBackups.Get(AsusLegacyUsbLighting.SagarisProduct));
            Assert.Null(AsusLegacyUsbLighting.ReadSagaris(new FakeReportChannel { Path = null, InputReportLength = 65, OutputReportLength = 65 }));
            Assert.Null(model.Begin(new FakeReportChannel { Path = null, InputReportLength = 65, OutputReportLength = 65 }, _ => { }));
            // An unanswered mode request is no state, though the palette
            // answers.
            Assert.Null(AsusLegacyUsbLighting.ReadSagaris(Keyboard(4, 2, 1, 3, answersMode: false)));
        }

        /// <summary>No picture goes out before its backup is in the settings
        /// file, as LenovoLegionLighting's Session.Paint waits.</summary>
        [Fact]
        public void APicture_WaitsForItsBackupToBeSaved()
        {
            KeyboardProfileBackups.ResetForTest();
            var io = Keyboard(4, 2, 1, 3);
            var session = Model().Begin(io, _ => { });
            io.Calls.Clear();
            Assert.Equal(HidLightingPaint.Later, session.Paint(io, _ => { }, Model().Map.Lights, new[] { 0xFFFFFF }));
            Assert.Equal(LenovoLegionLighting.SavingMs, session.LaterMs);
            Assert.Empty(io.Calls);
            Assert.Empty(session.CrashFrames);
            Assert.NotNull(KeyboardProfileBackups.Get(AsusLegacyUsbLighting.SagarisProduct));
        }

        /// <summary>A backup an earlier run left stands in for what the
        /// keyboard shows while it still shows PadForge's Static picture, and
        /// the session owes its restore from the start. Once something else
        /// set the keyboard, the backup is dropped.</summary>
        [Fact]
        public void AnEarlierRunsBackup_IsPutBack_OrDropped()
        {
            var saved = AsusLegacyUsbLighting.SagarisState.FromContents(
                new byte[] { 2, 1, 0, 5, 9, 9, 9, 8, 8, 8, 7, 7, 7, 6, 6, 6, 5, 5, 5, 4, 4, 4, 3, 3, 3 });
            KeyboardProfileBackups.Take(AsusLegacyUsbLighting.SagarisProduct, 0, saved.Contents());
            var model = Model();
            Assert.True(model.Owed(null));
            var session = (AsusLegacyUsbLighting.SagarisSession)model.Begin(Keyboard(1, 3, 0, 0), _ => { });
            Assert.True(session.State.SameAs(saved));
            Assert.Equal(8, session.CrashFrames.Length);

            KeyboardProfileBackups.Take(AsusLegacyUsbLighting.SagarisProduct, 0, saved.Contents());
            var other = (AsusLegacyUsbLighting.SagarisSession)model.Begin(Keyboard(4, 2, 1, 3), _ => { });
            Assert.False(other.State.SameAs(saved));
            Assert.Empty(other.CrashFrames);
            Assert.Null(KeyboardProfileBackups.Get(AsusLegacyUsbLighting.SagarisProduct));
            Assert.False(model.Owed(null));
        }

        /// <summary>The repair with no claim: the backup is written back while
        /// the keyboard shows PadForge's picture, dropped with nothing written
        /// once something else set it, and kept with a failure noted when the
        /// keyboard does not answer.</summary>
        [Fact]
        public void TheRepair_PutsBackOnlyAPictureOfPadForges()
        {
            var saved = new byte[] { 2, 1, 0, 5, 9, 9, 9, 8, 8, 8, 7, 7, 7, 6, 6, 6, 5, 5, 5, 4, 4, 4, 3, 3, 3 };
            var model = Model();
            KeyboardProfileBackups.Take(AsusLegacyUsbLighting.SagarisProduct, 0, saved);
            var io = Keyboard(1, 3, 0, 0);
            Assert.True(model.Repair(io, _ => { }));
            var outs = Outs(io);
            Assert.Equal(10, outs.Count);
            Starts("06 0B 04 00 00 09 09 09", outs[2], 65);
            Starts("06 0B 01 02 01 00 05", outs[9], 65);
            Assert.Null(KeyboardProfileBackups.Get(AsusLegacyUsbLighting.SagarisProduct));

            KeyboardProfileBackups.Take(AsusLegacyUsbLighting.SagarisProduct, 0, saved);
            io = Keyboard(4, 2, 1, 3);
            Assert.True(model.Repair(io, _ => { }));
            Assert.Equal(2, Outs(io).Count);
            Assert.Null(KeyboardProfileBackups.Get(AsusLegacyUsbLighting.SagarisProduct));

            KeyboardProfileBackups.Take(AsusLegacyUsbLighting.SagarisProduct, 0, saved);
            Assert.False(model.Repair(new FakeReportChannel { Path = null, InputReportLength = 65, OutputReportLength = 65 }, _ => { }));
            Assert.NotNull(KeyboardProfileBackups.Get(AsusLegacyUsbLighting.SagarisProduct));
            Assert.Equal(1, KeyboardProfileBackups.Failures(AsusLegacyUsbLighting.SagarisProduct));
        }
    }
}
