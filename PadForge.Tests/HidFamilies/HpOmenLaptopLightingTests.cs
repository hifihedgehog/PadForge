using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>HP's BIOS method, scripted as an Omen laptop's: it reports
    /// lighting and a keyboard type, keeps a 128-byte color table, and logs
    /// every call.</summary>
    internal sealed class FakeHpBios : IHpBiosChannel
    {
        public readonly List<(int Command, int Type, byte[] Input)> Calls = new();
        public byte Lighting = 1;
        public byte KeyboardType = 2;
        public bool Fails;
        public bool FailWrites;
        public bool Disposed;
        public byte[] Table;

        public FakeHpBios()
        {
            Table = new byte[128];
            Table[0] = 3;
            for (int i = 1; i < Table.Length; i++) Table[i] = (byte)(0x40 + i);
        }

        public byte[] Call(int command, int commandType, byte[] input)
        {
            Calls.Add((command, commandType, (byte[])input.Clone()));
            if (Fails) return null;
            switch ((command, commandType))
            {
                case (0x20009, 1): return Answer(Lighting);
                case (0x20008, 43): return Answer(KeyboardType);
                case (0x20009, 2): return (byte[])Table.Clone();
                case (0x20009, 3):
                    if (FailWrites) return null;
                    Table = (byte[])input.Clone();
                    return new byte[128];
                default: return null;
            }
        }

        private static byte[] Answer(byte first)
        {
            var answer = new byte[128];
            answer[0] = first;
            return answer;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>The HP Omen four-zone laptop family against OpenRGB's
    /// HPOmenLaptopController and OmenMon's color table (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class HpOmenLaptopLightingTests : IDisposable
    {
        public HpOmenLaptopLightingTests() => KeyboardProfileBackups.ResetForTest(persistAtOnce: true);

        public void Dispose() => KeyboardProfileBackups.ResetForTest();

        private static HpOmenLaptopLighting Family(FakeHpBios bios, bool classPresent = true,
            List<(int, int, byte[], int)> shots = null)
            => new(() => classPresent, () => bios, (c, t, frame, ms) => { shots?.Add((c, t, frame, ms)); return true; });

        [Fact]
        public void TheCommands_AreOpenRgbsAndOmenMons()
        {
            // execute(131081, ...) and execute(131080, 43, ...)
            // (HPOmenLaptopController_Windows.cpp:265, 278, 292, 308),
            // OmenMon's Keyboard and Default (BiosData.cs:22-27).
            Assert.Equal(131081, HpOmenLaptopLighting.KeyboardCommand);
            Assert.Equal(131080, HpOmenLaptopLighting.DefaultCommand);
            Assert.Equal((1, 2, 3, 43), (HpOmenLaptopLighting.HasLighting, HpOmenLaptopLighting.GetColorTable,
                HpOmenLaptopLighting.SetColorTable, HpOmenLaptopLighting.GetKeyboardType));
            Assert.Equal("SECU", System.Text.Encoding.ASCII.GetString(OwnLightingChannels.HpSign));
        }

        [Fact]
        public void TheLook_AsksForLightingAndTheKeyboardType_AndKeepsItsAnswer()
        {
            var bios = new FakeHpBios();
            var family = Family(bios);
            var device = Assert.Single(family.Look());
            Assert.Equal(@"ROOT\\WMI:hpqBIntM", device.Key);
            Assert.Equal(PeripheralLinker.LocalMachineContainer, device.Container);
            Assert.True(device.BuiltInKeyboard);
            Assert.Equal(new[] { (0x20009, 1), (0x20008, 43) }, bios.Calls.Select(c => (c.Command, c.Type)));
            Assert.All(bios.Calls, c => Assert.Empty(c.Input));
            Assert.True(bios.Disposed);
            Assert.Same(family.Look(), family.Look());
            Assert.Equal(2, bios.Calls.Count);
        }

        [Theory]
        [InlineData(0, 2)]
        [InlineData(1, 0)]
        [InlineData(1, 1)]
        [InlineData(1, 3)]
        public void OnlyALitTenKeyLessKeyboard_HasAUnit(byte lighting, byte type)
        {
            // OpenRGB's detector: lighting bit 0 set and type + 1 ==
            // WITHOUT_NUMPAD (HPOmenLaptopWMIDetect_Windows.cpp:20,
            // HPOmenLaptopController_Windows.cpp:294 and 310-312).
            var bios = new FakeHpBios { Lighting = lighting, KeyboardType = type };
            Assert.Empty(Family(bios).Look());
            Assert.Single(Family(new FakeHpBios { Lighting = 3, KeyboardType = 2 }).Look());
        }

        [Fact]
        public void APcWithoutHpsMethodClass_IsNeverCalled_AndABiosThatDidNotAnswer_IsAskedAgain()
        {
            var bios = new FakeHpBios();
            Assert.Empty(Family(bios, classPresent: false).Look());
            Assert.Empty(bios.Calls);

            bios.Fails = true;
            var family = Family(bios);
            Assert.Null(family.Look());
            bios.Fails = false;
            Assert.Single(family.Look());

            // No channel in the test host's real opener.
            Assert.Null(new HpOmenLaptopLighting(() => true, null, null).Look());
        }

        [Fact]
        public void TheLights_AreOpenRgbsZones_AtTheirPlacesInTheTable()
        {
            var lights = HpOmenLaptopLighting.Lights();
            // WASD, Left, Middle, Right (RGBController_HPOmenLaptopWMI_Windows.cpp:76-90)
            // at table zones 3, 2, 1, 0 (OmenMon BiosData.cs:100-106).
            Assert.Equal(new[] { 3, 2, 1, 0 }, lights.Select(l => l.Address));
            Assert.All(lights, l => Assert.Equal(LightKind.Zone, l.Kind));
        }

        [Fact]
        public void APicture_ReplacesEachZonesThreeBytesFrom25_AndNothingElse()
        {
            var table = new FakeHpBios().Table;
            var lights = HpOmenLaptopLighting.Lights();
            var painted = HpOmenLaptopLighting.Painted(table, lights, new[] { 0x0000AA, 0x00BB00, 0xCC0000, 0x123456 });
            // setColors: zone i gets colors[3 - i], red, green, blue from
            // byte 25 (HPOmenLaptopController_Windows.cpp:270-275).
            var expected = (byte[])table.Clone();
            byte[] right = { 0x12, 0x34, 0x56 }, middle = { 0xCC, 0, 0 }, left = { 0, 0xBB, 0 }, wasd = { 0, 0, 0xAA };
            right.CopyTo(expected, 25);
            middle.CopyTo(expected, 28);
            left.CopyTo(expected, 31);
            wasd.CopyTo(expected, 34);
            Assert.Equal(expected, painted);
            // OmenMon reads the same zones back (BiosData.cs:222-234).
            Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, painted.Skip(24 + 1).Take(3));
        }

        private long _now = 1_000_000;

        private HpOmenLaptopLighting Clocked(FakeHpBios bios)
            => new(() => true, () => bios, (_, _, _, _) => true, () => _now);

        private static readonly int[] OneColors = { 1, 2, 3, 4 };
        private static readonly int[] OtherColors = { 5, 6, 7, 8 };

        /// <summary>A forced kill and no later claim: the BIOS holds what PadForge
        /// last kept, so the first table goes back, with nothing left behind. A new
        /// family is what the next process has.</summary>
        [Fact]
        public void AKillWithNoLaterClaim_PutsTheFirstTableBack()
        {
            var bios = new FakeHpBios();
            var original = (byte[])bios.Table.Clone();
            var model = Clocked(bios).Model;
            var session = model.BeginOwn(model.Open("x"), _ => { });
            Assert.False(model.Owed("x"));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, model.Map.Lights, OneColors));
            Assert.True(model.Owed("x"));

            var next = Clocked(bios).Model;
            Assert.True(next.Owed("x"));
            Assert.True(next.RepairOwn(next.Open("x"), _ => { }));
            Assert.Equal(original, bios.Table);
            Assert.False(next.Owed("x"));
            Assert.Null(KeyboardProfileBackups.Get(HpOmenLaptopLighting.Product));
            Assert.Null(KeyboardProfileBackups.Get(HpOmenLaptopLighting.ShownKey));
        }

        /// <summary>The Hub stored colors after the kill, and no read can say
        /// otherwise of a table that differs: nothing is written, and it is not
        /// asked about again until the next session. A later claim still takes the
        /// backup.</summary>
        [Fact]
        public void AKillAndColorsThatDiffer_LeavesThemAlone()
        {
            var bios = new FakeHpBios();
            var original = (byte[])bios.Table.Clone();
            var model = Clocked(bios).Model;
            model.BeginOwn(model.Open("x"), _ => { }).Paint(null, _ => { }, model.Map.Lights, OneColors);
            var hub = HpOmenLaptopLighting.Painted(bios.Table, model.Map.Lights, OtherColors);
            bios.Table = (byte[])hub.Clone();
            int calls = bios.Calls.Count;

            var next = Clocked(bios).Model;
            Assert.True(next.RepairOwn(next.Open("x"), _ => { }));
            Assert.Equal(hub, bios.Table);
            Assert.DoesNotContain(bios.Calls.Skip(calls), c => c.Type == HpOmenLaptopLighting.SetColorTable);
            Assert.False(next.Owed("x"));

            var claim = next.BeginOwn(next.Open("x"), _ => { });
            Assert.Equal(original, Assert.Single(claim.CrashFrames));
            Assert.True(next.Owed("x"));
        }

        /// <summary>Only a picture that stands is kept: a save of the settings per
        /// frame of an animation is no price for this. A picture after a run of them
        /// is kept once it has stood, however soon it came.</summary>
        [Fact]
        public void APictureIsKept_OnceItHasStood()
        {
            var bios = new FakeHpBios();
            var original = (byte[])bios.Table.Clone();
            var model = Clocked(bios).Model;
            Assert.Equal(HpOmenLaptopLighting.ShownStandsMs, model.RecheckMs);
            var session = model.BeginOwn(model.Open("x"), _ => { });
            var lights = model.Map.Lights;
            session.Paint(null, _ => { }, lights, OneColors);
            var kept = KeyboardProfileBackups.Get(HpOmenLaptopLighting.ShownKey)!.Value.Contents;
            Assert.Equal(bios.Table, kept);

            // An animation: a look at the interval finds a picture too young.
            _now += HpOmenLaptopLighting.ShownStandsMs - 1;
            session.Paint(null, _ => { }, lights, OtherColors);
            _now += HpOmenLaptopLighting.ShownStandsMs - 1;
            Assert.True(session.Recheck(null, _ => { }));
            Assert.Equal(kept, KeyboardProfileBackups.Get(HpOmenLaptopLighting.ShownKey)!.Value.Contents);
            int calls = bios.Calls.Count;

            // It stood.
            _now += 1;
            Assert.True(session.Recheck(null, _ => { }));
            Assert.Equal(bios.Table, KeyboardProfileBackups.Get(HpOmenLaptopLighting.ShownKey)!.Value.Contents);
            Assert.Equal(calls, bios.Calls.Count);

            Assert.True(session.HandBack(null, _ => { }));
            Assert.Equal(original, bios.Table);
            Assert.Null(KeyboardProfileBackups.Get(HpOmenLaptopLighting.ShownKey));
            Assert.False(model.Owed("x"));
        }

        /// <summary>The worker asks on the model's interval, on a channel of the family's
        /// own, with no picture to send.</summary>
        [Fact]
        public void TheWorker_KeepsAPictureThatStood()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralLightMaps.ResetForTest();
            try
            {
                var bios = new FakeHpBios();
                var family = new HpOmenLaptopLighting(() => true, () => bios, (_, _, _, _) => true, () => Interlocked.Read(ref _now));
                var row = Guid.NewGuid();
                using var backend = new HidLightingBackend(new IHidLightingFamily[] { family }, _ => null, _ => Guid.Empty,
                    () => Interlocked.Read(ref _now), (_, _, _) => true, (_, _, _) => true, ms => Interlocked.Add(ref _now, ms), idleTickMs: 5);
                backend.Start();
                backend.Rescan(Array.Empty<HidCollectionInfo>());
                PeripheralOutputs.PublishLinks(new LinkTable(new[]
                {
                    new DeviceLinks { Device = row, Lighting = new[] { new OutputPath(OutputFamily.HidLightingUnit, HpOmenLaptopLighting.Key) } },
                }));
                PeripheralOutputs.SetLighting(row, slot: 0, player: 1, 0x11, 0x22, 0x33);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    backend.Wake();
                    return KeyboardProfileBackups.Get(HpOmenLaptopLighting.ShownKey) != null;
                }, 30000), "the first picture went out and was kept");
                var first = KeyboardProfileBackups.Get(HpOmenLaptopLighting.ShownKey)!.Value.Contents;

                PeripheralOutputs.SetLighting(row, slot: 0, player: 1, 0x44, 0x55, 0x66);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    Interlocked.Add(ref _now, 1000);
                    backend.Wake();
                    return bios.Table[HpOmenLaptopLighting.ColorsAt] == 0x44;
                }, 30000), "the second picture went out");
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    Interlocked.Add(ref _now, 1000);
                    backend.Wake();
                    return !KeyboardProfileBackups.Get(HpOmenLaptopLighting.ShownKey)!.Value.Contents.SequenceEqual(first);
                }, 30000), "the worker kept the second once it had stood");
                Assert.Equal(bios.Table, KeyboardProfileBackups.Get(HpOmenLaptopLighting.ShownKey)!.Value.Contents);
            }
            finally
            {
                PeripheralOutputs.ResetForTest();
                PeripheralLightMaps.ResetForTest();
            }
        }

        /// <summary>A failed BIOS call of this process's left the colors, whatever was
        /// kept: this process wrote them, so the first table goes back with no later
        /// claim. A session's hand-back ends that.</summary>
        [Fact]
        public void AFailedCallWithNoLaterClaim_PutsTheFirstTableBack()
        {
            var bios = new FakeHpBios();
            var original = (byte[])bios.Table.Clone();
            var model = Clocked(bios).Model;
            var session = model.BeginOwn(model.Open("x"), _ => { });
            var lights = model.Map.Lights;
            session.Paint(null, _ => { }, lights, OneColors);
            session.Paint(null, _ => { }, lights, OtherColors);
            bios.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(null, _ => { }, lights, OneColors));
            bios.FailWrites = false;

            Assert.True(model.RepairOwn(model.Open("x"), _ => { }));
            Assert.Equal(original, bios.Table);
            Assert.False(model.Owed("x"));
        }

        [Fact]
        public void ASession_ReadsTheTable_WritesItBackWithTheColors_AndHandsTheFirstTableBack()
        {
            var bios = new FakeHpBios();
            var original = (byte[])bios.Table.Clone();
            var shots = new List<(int, int, byte[], int)>();
            var model = Family(bios, shots: shots).Model;
            Assert.Equal(new[] { VendorPrograms.OmenGamingHub }, model.Programs);
            var session = model.BeginOwn(model.Open(@"ROOT\\WMI:hpqBIntM"), _ => { });
            Assert.NotNull(session);
            Assert.Equal((0x20009, 2), (bios.Calls[^1].Command, bios.Calls[^1].Type));
            Assert.Empty(session.CrashFrames);

            var lights = model.Map.Lights;
            // The BIOS changed a byte outside the colors since the session
            // began: the write keeps it, since each picture reads the table
            // first, as setColors does.
            bios.Table[1] ^= 0xFF;
            var current = (byte[])bios.Table.Clone();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, lights, new[] { 1, 2, 3, 4 }));
            // A read, then the write of the whole table.
            Assert.Equal(new[] { (0x20009, 2), (0x20009, 3) }, bios.Calls.Skip(1).Select(c => (c.Command, c.Type)));
            Assert.Equal(128, bios.Calls[^1].Input.Length);
            Assert.Equal(HpOmenLaptopLighting.Painted(current, lights, new[] { 1, 2, 3, 4 }), bios.Table);
            Assert.Equal(original, Assert.Single(session.CrashFrames));

            // The crash path writes that table through a call of its own.
            Assert.True(model.WriteOnce(@"ROOT\\WMI:hpqBIntM", session.CrashFrames[0], 200));
            var shot = Assert.Single(shots);
            Assert.Equal((0x20009, 3, 200), (shot.Item1, shot.Item2, shot.Item4));
            Assert.Equal(original, shot.Item3);

            Assert.True(session.HandBack(null, _ => { }));
            Assert.Equal(original, bios.Table);
            Assert.Empty(session.CrashFrames);
            // Nothing more to give back.
            int calls = bios.Calls.Count;
            Assert.True(session.HandBack(null, _ => { }));
            Assert.Equal(calls, bios.Calls.Count);
        }

        [Fact]
        public void AFailedWrite_FailsThePicture_AndAFailedHandBackKeepsTheCrashFrames()
        {
            var bios = new FakeHpBios();
            var model = Family(bios).Model;
            var session = model.BeginOwn(model.Open("k"), _ => { });
            bios.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(null, _ => { }, model.Map.Lights, new[] { 1, 2, 3, 4 }));
            // A write that reports a failure may have landed.
            Assert.Single(session.CrashFrames);
            Assert.False(session.HandBack(null, _ => { }));
            Assert.Single(session.CrashFrames);

            // A table the BIOS did not give begins nothing.
            bios.Fails = true;
            Assert.Null(model.BeginOwn(model.Open("k"), _ => { }));
            Assert.Null(model.BeginOwn(new FakeAtkAcpi(), _ => { }));
        }
    }
}
