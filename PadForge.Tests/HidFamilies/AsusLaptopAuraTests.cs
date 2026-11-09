using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.AsusAuraUsbLightingTests;

namespace PadForge.Tests
{
    /// <summary>ASUS laptops past OpenRGB's list, against G-Helper's app/USB and
    /// asusctl's rog-aura (#557).</summary>
    public class AsusLaptopAuraTests
    {
        private static List<byte[]> Sets(FakeReportChannel io) => io.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).ToList();

        private static HidCollectionInfo Collection(ushort product, ushort page = 0xFF31, ushort feature = 64) => new()
        {
            Path = $@"\\?\hid#vid_0b05&pid_{product:x4}#fake", VendorId = 0x0B05, ProductId = product, UsagePage = page, Usage = 0x79,
            FeatureReportLength = feature, OutputReportLength = 64,
        };

        private static AsusLaptopMachine Machine(string product = "ROG Zephyrus G14", string board = "GA402RJ") => new(product, board);

        /// <summary>An answer of the firmware: the echo, then the kind at byte 9, the
        /// layout at 12 and the features at 13 and 14 (Aura.cs:332-336).</summary>
        private static byte[] Answered(byte kind, byte layout = 1, byte features1 = 0, byte features2 = 0)
        {
            var answer = new byte[64];
            answer[0] = 0x5D;
            answer[1] = 0x05;
            answer[2] = 0x20;
            answer[3] = 0x31;
            answer[9] = kind;
            answer[12] = layout;
            answer[13] = features1;
            answer[14] = features2;
            return answer;
        }

        private static FakeReportChannel Answering(byte[] answer)
        {
            var io = new FakeReportChannel { FeatureReportLength = 64, OutputReportLength = 64 };
            io.Feature = id => id == 0x5D ? answer : null;
            return io;
        }

        [Fact]
        public void TheProducts_AreGHelpersList_LessWhatOpenRgbHolds()
        {
            int[] gHelper =
            {
                0x1a30, 0x1854, 0x1869, 0x1866, 0x19b6, 0x1822, 0x1837, 0x1854, 0x184a, 0x183d, 0x8502, 0x1807, 0x17e0, 0x1abe, 0x1b4c,
                0x1b6e, 0x1b2c, 0x8854, 0x1CE7, 0x1ce6, 0x1bf2, 0x1bf3, 0x1cd7, 0x1cd8, 0x4b42,
            };
            int[] openRgb = { 0x1854, 0x1866, 0x1869, 0x1ABE, 0x1B4C };
            Assert.Equal(gHelper.Except(openRgb).OrderBy(p => p), AsusLaptopAura.Products.Select(p => (int)p).OrderBy(p => p));
        }

        [Fact]
        public void EveryProduct_IsMatchedOnItsAuraPage_AndNowhereElse()
        {
            foreach (ushort product in AsusLaptopAura.Products)
            {
                var family = new AsusAuraCoreLighting(() => "GA402RJ", () => "ROG Zephyrus G14");
                var model = family.Match(Collection(product));
                Assert.Equal("ASUS Laptop Keyboard", model.Name);
                Assert.True(model.LiveThroughLampArray);
                Assert.Equal(new[] { VendorPrograms.ArmouryCrate }, model.Programs);
                Assert.NotNull(model.Identify);
                Assert.Equal(0, model.HoldMs);
                Assert.Equal(HidppBackend.PaintMs, model.RepaintMs);
                Assert.Null(family.Match(Collection(product, page: 0x0001)));
                Assert.Null(family.Match(Collection(product, feature: 0)));
            }
        }

        /// <summary>No other family on the worker's list takes one of these products,
        /// and the Aura Core family comes to them.</summary>
        [Fact]
        public void NoOtherFamily_HoldsTheseProducts()
        {
            foreach (ushort product in AsusLaptopAura.Products.Append(AsusLaptopAura.RearLightProduct))
                foreach (var family in HidLightingFamilies.All)
                    if (!ReferenceEquals(family, AsusAuraCoreLighting.Instance))
                        Assert.Null(family.Match(Collection(product)));
        }

        /// <summary>G-Helper lights these through ACPI (isACPI, Aura.cs:108).</summary>
        [Theory]
        [InlineData("ASUS TUF Gaming F15 FX507ZM")]
        [InlineData("TX Gaming A15")]
        [InlineData("TX Air 15")]
        [InlineData("Vivobook Pro 16X")]
        [InlineData("ASUS Zenbook 14")]
        [InlineData("ProArt P16")]
        [InlineData("ASUSLaptop X515")]
        [InlineData("ASUS EXPERTBOOK B9")]
        [InlineData("ASUS V16 V3607")]
        public void ALaptopLitThroughAcpi_IsNotMatched(string product)
            => Assert.Null(new AsusAuraCoreLighting(() => "BOARD", () => product).Match(Collection(0x19B6)));

        /// <summary>OpenRGB's rows come first: a board it lists keeps its layouts.</summary>
        [Fact]
        public void ABoardOpenRgbLists_KeepsOpenRgbsRow()
        {
            var row = OpenRgbRegistrations.Of(AsusAuraCoreLighting.OpenRgbFamily).Single(r => r.Name == "ASUS ROG Strix SCAR 15");
            var collection = CollectionOf(row);
            Assert.Equal("ASUS ROG Strix SCAR 15", new AsusAuraCoreLighting(() => "G533ZM", () => "ROG Strix SCAR 15").Match(collection).Name);
            Assert.Equal("ASUS Laptop Keyboard", new AsusAuraCoreLighting(() => "GA402RJ", () => "ROG Zephyrus G14").Match(collection).Name);
        }

        /// <summary>The Z13's rear light has one light and the stored-write bounds,
        /// and on any other laptop that product is a keyboard (HasRearLight, AppConfig.cs:599-602).</summary>
        [Fact]
        public void TheRearLight_OfTheZ13_IsOneStoredLight()
        {
            var rear = new AsusAuraCoreLighting(() => "GZ301ZE", () => "ROG Flow Z13 GZ301ZE").Match(Collection(0x18C6));
            Assert.Equal("ASUS Laptop Rear Light", rear.Name);
            Assert.False(rear.LiveThroughLampArray);
            Assert.Equal(LenovoLegionLighting.HoldMs, rear.HoldMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, rear.RepaintMs);
            Assert.Single(rear.Map.Lights);
            var keyboard = new AsusAuraCoreLighting(() => "G533QS", () => "ROG Strix G533QS").Match(Collection(0x18C6));
            Assert.Equal("ASUS Laptop Keyboard", keyboard.Name);

            var io = new FakeReportChannel { FeatureReportLength = 64 };
            var session = AsusLaptopAura.Begin(io, _ => { }, 0x18C6, new AsusLaptopMachine("ROG Flow Z13", "GZ301ZE"));
            Assert.Empty(Sets(io));
            session.Paint(io, _ => { }, session.Map.Lights, new[] { 0x112233 });
            var sets = Sets(io);
            Assert.Equal(3, sets.Count);
            Starts("5D B3 00 00 11 22 33 EB 00 00 11 22 33", sets[0], 64);
            Starts("5D B5", sets[1], 64);
            Starts("5D B4", sets[2], 64);
            Assert.Equal(LenovoLegionLighting.HoldMs, session.HoldMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, session.RepaintMs);
        }

        /// <summary>The identity check reads and writes nothing.</summary>
        [Fact]
        public void TheIdentityCheck_OnlyReadsReport5D()
        {
            var io = Answering(new byte[] { 0x5D });
            Assert.True(AsusLaptopAura.AnswersAura(io));
            Assert.Equal(("get", (byte)0x5D), (io.Calls.Single().Kind, io.Calls.Single().Bytes[0]));
            Assert.False(AsusLaptopAura.AnswersAura(new FakeReportChannel()));
            Assert.False(AsusLaptopAura.AnswersAura(null));
        }

        /// <summary>AuraProbe (AsusHid.cs:254-296): B9, the ID string, the query, then
        /// the read, whose first four bytes echo it.</summary>
        [Fact]
        public void TheQuery_IsGHelpers_AndAnAnswerMustEchoIt()
        {
            var io = Answering(Answered(3, 2, 0x82, 0x10));
            var answer = AsusLaptopAura.Ask(io);
            var sets = Sets(io);
            Assert.Equal(3, sets.Count);
            Starts("5D B9", sets[0], 64);
            Starts("5D 41 53 55 53 20 54 65 63 68 2E 49 6E 63 2E", sets[1], 64);
            Starts("5D 05 20 31 00 20", sets[2], 64);
            Assert.Equal(new AsusLaptopAura.Answer(3, 2, 0x82, 0x10), answer);

            var wrong = Answered(3);
            wrong[3] = 0x32;
            Assert.Null(AsusLaptopAura.Ask(Answering(wrong)));
            Assert.Null(AsusLaptopAura.Ask(new FakeReportChannel { FeatureReportLength = 64 }));
        }

        /// <summary>G-Helper writes the query as output reports, so a feature write that
        /// fails goes that way. A collection with neither gives no answer.</summary>
        [Fact]
        public void AFeatureWriteThatFails_GoesOutAsAnOutputReport()
        {
            var io = new FailingFeatureChannel(Answered(4));
            Assert.Equal(new AsusLaptopAura.Answer(4, 1, 0, 0), AsusLaptopAura.Ask(io));
            Assert.Equal(3, io.Outputs);
            Assert.Null(AsusLaptopAura.Ask(new FailingFeatureChannel(Answered(4)) { Output = 0 }));
        }

        private sealed class FailingFeatureChannel : IHidReportChannel
        {
            private readonly byte[] _answer;
            public int Outputs;
            public int Output = 64;

            public FailingFeatureChannel(byte[] answer) => _answer = answer;

            public string Path => "fake";
            public bool FeatureOnly => false;
            public int InputReportLength => 0;
            public int OutputReportLength => Output;
            public int FeatureReportLength => 64;
            public bool SetFeature(byte[] report) => false;

            public bool GetFeature(byte reportId, byte[] buffer)
            {
                _answer.CopyTo(buffer, 0);
                return true;
            }

            public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
            {
                Outputs++;
                return true;
            }

            public int ReadInput(byte[] buffer, int timeoutMs) => 0;
            public void Dispose() { }
        }

        private static readonly AsusLaptopBoard PerKeyRow = new("X", "", "g513i-per-key", true, false);
        private static readonly AsusLaptopBoard ZonesRow = new("X", "", "g513i", false, true);
        private static readonly AsusLaptopBoard PlainRow = new("X", "", "ga401q", false, false);

        /// <summary>The firmware's word first (Aura.cs:362-369), then asusctl's list,
        /// then G-Helper's lists of keyboards that take B3.</summary>
        [Fact]
        public void TheKind_GoesByTheFirmware_ThenAsusctl_ThenGHelper()
        {
            var plain = Machine();
            AsusLaptopAura.Answer Says(byte kind, byte features2 = 0) => new(kind, 1, 0, features2);
            Assert.Equal(AsusLaptopAura.Kind.PerKey, AsusLaptopAura.KindOf(Says(3), plain, null));
            Assert.Equal(AsusLaptopAura.Kind.PerKey, AsusLaptopAura.KindOf(Says(3), plain, ZonesRow));
            Assert.Equal(AsusLaptopAura.Kind.Zones, AsusLaptopAura.KindOf(Says(2), plain, PerKeyRow));
            Assert.Equal(AsusLaptopAura.Kind.PerKey, AsusLaptopAura.KindOf(Says(4), plain, PerKeyRow));
            Assert.Equal(AsusLaptopAura.Kind.PerKey, AsusLaptopAura.KindOf(null, plain, PerKeyRow));
            Assert.Equal(AsusLaptopAura.Kind.Zones, AsusLaptopAura.KindOf(null, plain, ZonesRow));
            Assert.Equal(AsusLaptopAura.Kind.Zones, AsusLaptopAura.KindOf(Says(0), plain, ZonesRow));
            // The firmware says one zone, and asusctl's four zones do not outweigh it.
            Assert.Equal(AsusLaptopAura.Kind.Whole, AsusLaptopAura.KindOf(Says(4), plain, ZonesRow));
            Assert.Equal(AsusLaptopAura.Kind.Whole, AsusLaptopAura.KindOf(Says(4), plain, PlainRow));
            Assert.Equal(AsusLaptopAura.Kind.Whole, AsusLaptopAura.KindOf(null, plain, null));
            // IsNoDirectRGB and IsSlash.
            foreach (string name in new[] { "GA503QM", "G533QS", "GU502LU", "GA403UV", "GU605MI", "GA605WI", "GA405", "GU405", "GU606", "GX651" })
                Assert.Equal(AsusLaptopAura.Kind.Static, AsusLaptopAura.KindOf(Says(4), new AsusLaptopMachine(null, name), null));
            // The same list loses to a keyboard that says per-key: G-Helper's array
            // path has no such check (Aura.cs:651).
            Assert.Equal(AsusLaptopAura.Kind.PerKey, AsusLaptopAura.KindOf(Says(3), new AsusLaptopMachine(null, "G533QS"), null));
            // White only, by name or by the firmware's bit when it names a kind.
            foreach (string name in new[] { "GA401QM", "FX517ZM", "FX516PR", "ROG Flow X13", "GZ301VIC", "FA617NS", "FA617XS", "GA502IU", "HN7306", "H7604", "M6500XV" })
                Assert.Equal(AsusLaptopAura.Kind.Static, AsusLaptopAura.KindOf(null, new AsusLaptopMachine(name, null), null));
            Assert.Equal(AsusLaptopAura.Kind.Static, AsusLaptopAura.KindOf(Says(4, 0x10), plain, null));
            Assert.Equal(AsusLaptopAura.Kind.Whole, AsusLaptopAura.KindOf(Says(0, 0x10), plain, null));
        }

        /// <summary>match_device: from the end of the list, the first name the board
        /// holds, with its product where it names one (aura_detection.rs).</summary>
        [Fact]
        public void TheBoard_IsFoundAsAsusctlFindsIt()
        {
            Assert.Null(AsusLaptopAura.BoardOf(null, 0x19B6));
            Assert.Null(AsusLaptopAura.BoardOf("NOT-A-BOARD", 0x19B6));
            Assert.Equal("G513QR", AsusLaptopAura.BoardOf("G513QR", 0x19B6).Name);
            // A longer name further down the list wins over a shorter one above it.
            Assert.Equal("G614JIR", AsusLaptopAura.BoardOf("G614JIR", 0x19B6).Name);
            Assert.Equal("g513i-per-key", AsusLaptopAura.BoardOf("G614JIR", 0x19B6).Layout);
            Assert.Equal("g634j-per-key", AsusLaptopAura.BoardOf("G614JI", 0x19B6).Layout);
            // Products: G533Q on 1866 and G533QS on 18C6.
            Assert.Equal("1866", AsusLaptopAura.BoardOf("G533Q", 0x1866).Product);
            Assert.Null(AsusLaptopAura.BoardOf("G533Q", 0x1234));
            Assert.Equal("18c6", AsusLaptopAura.BoardOf("G533QS", 0x18C6).Product);
            // Case counts: the list is compared as asusctl compares it.
            Assert.Null(AsusLaptopAura.BoardOf("g513qr", 0x19B6));
        }

        [Fact]
        public void TheFrames_AreGHelpersBytes()
        {
            Starts("5D BC 01 01 00 00 00 00 00 11 22 33", AsusLaptopAura.WholeFrame(0x112233), 64);

            var zones = AsusLaptopAura.ZonesMap(0x19B6, true).Lights;
            Assert.Equal(10, zones.Count);
            var colors = Enumerable.Range(1, 10).Select(i => i * 0x010101).ToList();
            Starts("5D BC 00 01 04 00 00 00 00 01 01 01 02 02 02 03 03 03 04 04 04 00 00 00 00 00 00 05 05 05 06 06 06 07 07 07 08 08 08 09 09 09 0A 0A 0A",
                AsusLaptopAura.ZonesFrame(zones, colors), 64);

            var message = AsusLaptopAura.StaticFrames(0x112233, false, false);
            Assert.Equal(2, message.Count);
            Starts("5D B3 00 00 11 22 33 EB 00 00 11 22 33", message[0], 64);
            Starts("5D B5", message[1], 64);
            // Black takes FF at byte 9 (Aura.cs:311).
            Starts("5D B3 00 00 00 00 00 EB 00 FF", AsusLaptopAura.StaticFrames(0, false, false)[0], 64);
            // A white keyboard's level is the brightest channel, in the red byte.
            Starts("5D B3 00 00 44 00 00 EB 00 00 44 00 00", AsusLaptopAura.StaticFrames(0x114422, true, false)[0], 64);
            Starts("5D B4", AsusLaptopAura.StaticFrames(0x112233, false, true)[2], 64);
        }

        /// <summary>SetLedsDirect's frames (Aura.cs:714-742) and the rear strip's
        /// (lines 752-766).</summary>
        [Fact]
        public void APerKeyPicture_IsTheKeys_ThePast_AndTheRearStrip()
        {
            var lights = new[]
            {
                new LightInfo(21, LightKind.Key, 0x29, LightKeySource.Generated),
                new LightInfo(166, LightKind.Key, 0, LightKeySource.None),
                new LightInfo(167, LightKind.Logo, 0, LightKeySource.None),
                new LightInfo(177, LightKind.Edge, 0, LightKeySource.None),
                new LightInfo(AsusLaptopAura.RearStripFirstAddress, LightKind.Edge, 0, LightKeySource.None),
                new LightInfo(AsusLaptopAura.RearStripFirstAddress + 38, LightKind.Edge, 0, LightKeySource.None),
            };
            var colors = new[] { 0x010203, 0x040506, 0x070809, 0x0A0B0C, 0x0D0E0F, 0x101112 };
            var plain = AsusLaptopAura.Frames(AsusLaptopAura.Kind.PerKey, lights, colors, false, false, false);
            Assert.Equal(12, plain.Count);
            Starts("5D BC 00 01 01 01 00 10 00", plain[0], 64);
            Starts("5D BC 00 01 01 01 10 10 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 01 02 03", plain[1], 64);
            // Seven keys in the last frame of keys: 160 to 166.
            Starts("5D BC 00 01 01 01 A0 07 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 04 05 06", plain[10], 64);
            Assert.Equal("5D BC 00 01 04 00 00 00 00 07 08 09", Hex(plain[11].Take(12).ToArray()));
            Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C }, plain[11].Skip(9 + 10 * 3).Take(3).ToArray());

            var rear = AsusLaptopAura.Frames(AsusLaptopAura.Kind.PerKey, lights, colors, false, false, true);
            Assert.Equal(15, rear.Count);
            Assert.Equal("5D BC 00 01 05 00 01 10 00 0D 0E 0F", Hex(rear[12].Take(12).ToArray()));
            Starts("5D BC 00 01 05 00 11 10 00", rear[13], 64);
            Assert.Equal("5D BC 00 01 05 00 21 07 00", Hex(rear[14].Take(9).ToArray()));
            Assert.Equal(new byte[] { 0x10, 0x11, 0x12 }, rear[14].Skip(9 + 6 * 3).Take(3).ToArray());
        }

        /// <summary>A keyboard that says per-key, ISO, with a rear strip.</summary>
        [Fact]
        public void APerKeyKeyboard_OpensWithBc00_AndTakesItsLayout()
        {
            var io = Answering(Answered(3, 2, 0x80));
            int waited = 0;
            var session = AsusLaptopAura.Begin(io, ms => waited += ms, 0x19B6, new AsusLaptopMachine("ROG Strix", "G513QR"));
            var sets = Sets(io);
            Assert.Equal(6, sets.Count);
            Starts("5D BC", sets[3], 64);
            Starts("5D BA C5 C4 03", sets[4], 64);
            Starts("5D BC", sets[5], 64);
            Assert.Equal(50, waited);
            var lights = session.Map.Lights;
            Assert.Equal(0x32, lights.Single(l => l.Address == 96).Usage);
            Assert.Equal(0x64, lights.Single(l => l.Address == 106).Usage);
            Assert.Equal(AsusLaptopAura.RearStripLeds, lights.Count(l => l.Address >= AsusLaptopAura.RearStripFirstAddress));
            Assert.Equal(0x29, lights.Single(l => l.Address == 21).Usage);
            Assert.Equal(0, session.HoldMs);
            Assert.Equal(HidppBackend.PaintMs, session.RepaintMs);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Empty(session.CrashFrames);

            int before = Sets(io).Count;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, lights.Select(_ => 0x112233).ToList()));
            Assert.Equal(15, Sets(io).Count - before);
        }

        /// <summary>On ANSI, the same two LEDs sit under Enter and Left Shift.</summary>
        [Fact]
        public void AnAnsiKeyboard_LightsTheLedsUnderItsLongKeys()
        {
            var lights = AsusLaptopAura.PerKeyMap(0x19B6, AsusLaptopTables.Default(), false, false).Lights;
            Assert.Equal(0x28, lights.Single(l => l.Address == 96).Usage);
            Assert.Equal(0xE1, lights.Single(l => l.Address == 106).Usage);
        }

        /// <summary>Four zones open with BC 01, or BC 00 on an old Strix (Aura.cs:378 and
        /// 688 at 991145fc), and name a lightbar when the firmware does, as an old Strix
        /// always does (Aura.cs:379). G-Helper's S17 rule went with its per-key commit
        /// 49dbbe1, so an S17 opens as any other four-zone keyboard.</summary>
        [Fact]
        public void AZonedKeyboard_OpensByItsAge_AndShowsItsLightbar()
        {
            var machine = Machine();
            var io = Answering(Answered(2, 1, 0x02));
            var session = AsusLaptopAura.Begin(io, _ => { }, 0x19B6, machine);
            Starts("5D BC 01", Sets(io)[3], 64);
            Assert.Equal(10, session.Map.Lights.Count);

            io = Answering(Answered(2, 1, 0x01));
            session = AsusLaptopAura.Begin(io, _ => { }, 0x19B6, machine);
            Assert.Equal(4, session.Map.Lights.Count);

            io = Answering(Answered(2));
            session = AsusLaptopAura.Begin(io, _ => { }, 0x19B6, new AsusLaptopMachine("ROG Strix G15", "G513"));
            Starts("5D BC", Sets(io)[3], 64);
            Assert.Equal(10, session.Map.Lights.Count);

            io = Answering(Answered(2, 1, 0x01));
            AsusLaptopAura.Begin(io, _ => { }, 0x19B6, new AsusLaptopMachine("ROG S17", "X"));
            Assert.Equal(0x01, Sets(io)[3][2]);
        }

        /// <summary>One zone, and the Z13's opening frame (Aura.cs:395-396).</summary>
        [Fact]
        public void OneZone_IsOneLight_AndTheZ13TakesItsOpening()
        {
            var io = Answering(Answered(4));
            var session = AsusLaptopAura.Begin(io, _ => { }, 0x1A30, new AsusLaptopMachine("ROG Flow Z13", "GZ302EA"));
            // The opening frame goes as an output report, the rest as feature reports.
            var sets = Sets(io);
            Assert.Equal(6, sets.Count);
            Starts("5D C0 03 01", io.Calls.Single(c => c.Kind == "out").Bytes, 64);
            Starts("5D BC 01", sets[3], 64);
            Starts("5D BA C5 C4 03", sets[4], 64);
            Starts("5D BC 01", sets[5], 64);
            Assert.Single(session.Map.Lights);
            int before = Sets(io).Count;
            session.Paint(io, _ => { }, session.Map.Lights, new[] { 0x112233 });
            Starts("5D BC 01 01 00 00 00 00 00 11 22 33", Sets(io)[before], 64);
        }

        /// <summary>B3 takes no opening and repaints at G-Helper's interval (Aura.cs:966).</summary>
        [Fact]
        public void ANoDirectKeyboard_OpensWithNothing_AndRepaintsSlowly()
        {
            var io = Answering(Answered(4));
            var session = AsusLaptopAura.Begin(io, _ => { }, 0x19B6, new AsusLaptopMachine("ROG Zephyrus G15", "GA503QM"));
            Assert.Equal(3, Sets(io).Count);
            Assert.Equal(AsusLaptopAura.StaticRepaintMs, session.RepaintMs);
            Assert.Equal(0, session.HoldMs);
            session.Paint(io, _ => { }, session.Map.Lights, new[] { 0x112233 });
            Assert.Equal(5, Sets(io).Count);
        }

        [Fact]
        public void AWriteThatFails_EndsTheOpeningAndThePicture()
        {
            var io = new FakeReportChannel { FeatureReportLength = 64, FailWrites = true };
            Assert.Null(AsusLaptopAura.Open(io, _ => { }, 0x19B6, Machine(), new AsusLaptopAura.Answer(4, 1, 0, 0)));
            var good = Answering(Answered(4));
            var session = AsusLaptopAura.Begin(good, _ => { }, 0x19B6, Machine());
            good.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(good, _ => { }, session.Map.Lights, new[] { 1 }));
            Assert.Null(AsusLaptopAura.Begin(null, _ => { }, 0x19B6, Machine()));
        }

        /// <summary>A board asusctl lists shows its keys before any session has asked.</summary>
        [Fact]
        public void AListedPerKeyBoard_ShowsItsKeysFromTheStart()
        {
            var map = AsusLaptopAura.FirstMap(0x19B6, new AsusLaptopMachine(null, "G614JI"));
            Assert.Contains(map.Lights, l => l.Usage == 0x29);
            Assert.Single(AsusLaptopAura.FirstMap(0x19B6, new AsusLaptopMachine(null, "GA402RJ")).Lights);
            Assert.Single(AsusLaptopAura.FirstMap(0x19B6, AsusLaptopMachine.Unknown).Lights);
        }

        // ── OpenRGB's Aura Core products ──

        /// <summary>A keyboard on 1854, 1866 or 1869 whose firmware says what it is opens
        /// in direct mode. One that says nothing stays with OpenRGB's Static per zone.</summary>
        [Fact]
        public void AnAuraCoreKeyboard_ThatSaysWhatItIs_OpensDirect()
        {
            var machine = Machine();
            var io = Answering(Answered(4));
            var session = AsusAuraCoreLighting.BeginCore(io, _ => { }, 0x1854, null, machine);
            Assert.IsType<AsusLaptopAura.Session>(session);
            Assert.Single(session.Map.Lights);

            io = Answering(Answered(2));
            Assert.Equal(4, AsusAuraCoreLighting.BeginCore(io, _ => { }, 0x1869, null, machine).Map.Lights.Count);

            io = Answering(Answered(3));
            Assert.True(AsusAuraCoreLighting.BeginCore(io, _ => { }, 0x1866, null, machine).Map.Lights.Count > 90);

            // The firmware says 00, or nothing: OpenRGB's way.
            Assert.IsType<AsusAuraCoreLighting.KeyboardSession>(AsusAuraCoreLighting.BeginCore(Answering(Answered(0)), _ => { }, 0x1854, null, machine));
            var wrong = Answered(3);
            wrong[1] = 0;
            Assert.IsType<AsusAuraCoreLighting.KeyboardSession>(AsusAuraCoreLighting.BeginCore(Answering(wrong), _ => { }, 0x1854, null, machine));

            // Silent firmware on a board asusctl calls per-key.
            io = Answering(Answered(0));
            session = AsusAuraCoreLighting.BeginCore(io, _ => { }, 0x1866, null, new AsusLaptopMachine(null, "G533Q"));
            Assert.IsType<AsusLaptopAura.Session>(session);
            Assert.True(session.Map.Lights.Count > 90);
        }

        /// <summary>A board OpenRGB lists keeps its layouts where the keyboard is per-key.</summary>
        [Fact]
        public void AnAuraCoreKeyboard_OnABoardOpenRgbLists_KeepsItsLayouts()
        {
            var rows = AsusAuraCoreLighting.LaptopRows("G533ZM");
            var session = AsusAuraCoreLighting.BeginCore(Answering(Answered(3)), _ => { }, 0x1866, rows, new AsusLaptopMachine(null, "G533ZM"));
            Assert.IsType<AsusAuraCoreLighting.LaptopSession>(session);
        }

        /// <summary>G-Helper never sends the query to a laptop it lights through ACPI.</summary>
        [Fact]
        public void AnAcpiLaptop_OnAnAuraCoreProduct_GetsNoQuery()
        {
            var io = Answering(Answered(3));
            var session = AsusAuraCoreLighting.BeginCore(io, _ => { }, 0x1866, null, new AsusLaptopMachine("ASUS TUF Gaming F15", "FX506"));
            Assert.IsType<AsusAuraCoreLighting.KeyboardSession>(session);
            Assert.Empty(Sets(io));
        }

        // ── The tables ──

        [Fact]
        public void TheBoardList_IsAsusctlsWhole()
        {
            Assert.Equal(125, AsusLaptopTables.Boards.Length);
            Assert.Equal(38, AsusLaptopTables.Boards.Count(b => b.PerKey));
            Assert.Equal("321b1114a0ac3782d67aa39201682effb749f4d9", AsusLaptopTables.AsusctlCommit);
            Assert.Equal("991145fc25c2bd0b3345f334f1fdeb8b4172322a", AsusLaptopTables.GHelperCommit);
            foreach (var board in AsusLaptopTables.Boards.Where(b => b.PerKey))
                Assert.NotNull(AsusLaptopTables.Layout(board.Layout));
            Assert.Null(AsusLaptopTables.Layout("no such layout"));
        }

        public static IEnumerable<object[]> PerKeyTables()
        {
            foreach (string layout in AsusLaptopTables.Boards.Where(b => b.PerKey).Select(b => b.Layout).Distinct())
                yield return new object[] { layout };
            yield return new object[] { "" };
        }

        private static PluginKeyTable TableOf(string layout) => layout.Length == 0 ? AsusLaptopTables.Default() : AsusLaptopTables.Layout(layout);

        /// <summary>Each LED once, on the picture's numbers, and the keys every laptop
        /// keyboard has at G-Helper's numbers, whose first frames OpenRGB shares.</summary>
        [Theory]
        [MemberData(nameof(PerKeyTables))]
        public void EveryTable_NumbersEachLedOnce_AndHasTheKeysAtTheirNumbers(string layout)
        {
            var table = TableOf(layout);
            Assert.Equal(table.Indexes.Length, table.Indexes.Distinct().Count());
            Assert.All(table.Indexes, led => Assert.InRange(led, 0, AsusLaptopAura.PerKeyLeds - 1));
            Assert.Equal(table.Names.Length, table.Indexes.Length);
            Assert.Equal(table.X.Length, table.Indexes.Length);
            Assert.Equal(table.Y.Length, table.Indexes.Length);
            var lights = PluginKeyNames.Lights(table);
            int At(ushort usage) => lights.First(l => l.Usage == usage).Address;
            Assert.Equal(21, At(0x29)); // Esc
            Assert.Equal(23, At(0x3A)); // F1
            Assert.Equal(36, At(0x45)); // F12
            Assert.Equal(43, At(0x1E)); // 1
            Assert.Equal(64, At(0x14)); // Q
            Assert.Equal(85, At(0x04)); // A
            Assert.Equal(107, At(0x1D)); // Z
            Assert.Equal(126, At(0xE0)); // Left Ctrl
            var arrows = layout switch
            {
                "g634j-per-key" or "g635l-per-key" => (Up: 120, Left: 140, Down: 141, Right: 142),
                "g814ji-per-key" => (Up: 121, Left: 141, Down: 142, Right: 143),
                _ => (Up: 139, Left: 159, Down: 160, Right: 161),
            };
            Assert.Equal(arrows.Up, At(0x52));
            Assert.Equal(arrows.Left, At(0x50));
            Assert.Equal(arrows.Down, At(0x51));
            Assert.Equal(arrows.Right, At(0x4F));
            Assert.Contains(lights, l => l.Usage == 0x2C && l.Address == 131); // Space
        }

        /// <summary>Key by key, OpenRGB's laptops and these tables agree: every key OpenRGB
        /// places on a laptop has a light at the same LED and for the same key in the table
        /// of the same family. OpenRGB's 136 appears twice, as Right Windows and Menu, and
        /// its 0 stands for a key it does not address. Its LED 96 is the ISO key, which on
        /// ANSI sits under Enter here, and the same for 106. A row-end key has a different
        /// name by laptop.</summary>
        [Theory]
        [InlineData("G533ZM", "g533q-per-key")]
        [InlineData("G533ZW", "g533q-per-key")]
        [InlineData("G614JZ", "g634j-per-key")]
        [InlineData("G814JV", "g814ji-per-key")]
        [InlineData("G814JIR", "g814ji-per-key")]
        public void OpenRgbsLaptops_AgreeWithTheTable(string board, string layout)
        {
            var known = PluginKeyNames.Lights(AsusLaptopTables.Layout(layout)).ToLookup(l => l.Address);
            var rows = AsusAuraCoreLighting.LaptopRows(board);
            var map = AsusAuraCoreLighting.LaptopMap(0x19B6, rows, KeyboardLayout.AnsiQwerty, out _);
            int compared = 0;
            var differs = new List<string>();
            foreach (var light in map.Lights.Where(l => l.Kind == LightKind.Key && l.Usage != 0 && l.Address != 0 && l.Address != 136
                         && l.Address != 96 && l.Address != 106 && l.Address is not (58 or 79 or 100 or 121)))
            {
                if (known[light.Address].Any(k => k.Usage == light.Usage)) compared++;
                else differs.Add($"LED {light.Address} usage {light.Usage:X2}");
            }
            Assert.True(differs.Count == 0, $"{board}: {string.Join(", ", differs)}");
            Assert.True(compared > 70, $"{compared} keys compared on {board}");
        }

        // HID usages of the keys these tests name.
        private const ushort Up = 0x52, Left = 0x50, Down = 0x51, Right = 0x4F, Dot = 0x37, Slash = 0x38, NumLock = 0x53, NumSlash = 0x54,
            RightShift = 0xE5, PrintScreen = 0x46;

        /// <summary>asusctl's g733pz and g814ji files put the number pad's row first, so a
        /// code's first time is not always the main key. The number pad's "/" follows NumLock
        /// at G-Helper's 60, and the main "/" follows "." at 116 (app/USB/Aura.cs:560-582).</summary>
        [Theory]
        [InlineData("g733pz-per-key")]
        [InlineData("g814ji-per-key")]
        public void TheTwoSlashKeys_SitWhereTheirKeysAre(string layout)
        {
            var lights = PluginKeyNames.Lights(TableOf(layout));
            LightInfo Key(ushort usage) => lights.First(l => l.Usage == usage);
            Assert.Equal(116, Key(Slash).Address);
            Assert.Equal(Key(Dot).Y, Key(Slash).Y);
            Assert.True(Key(Slash).X > Key(Dot).X);
            Assert.True(Key(Slash).X < Key(RightShift).X);
            Assert.Equal(60, Key(NumSlash).Address);
            Assert.Equal(Key(NumLock).Y, Key(NumSlash).Y);
            Assert.True(Key(NumSlash).X > Key(NumLock).X);
        }

        /// <summary>The keyboard-order rules every plugin's list is held to, on each key's
        /// first light: a second LED under a key sits apart by design.</summary>
        [Theory]
        [MemberData(nameof(PerKeyTables))]
        public void EveryTable_KeepsItsKeysInKeyboardOrder(string layout)
        {
            var table = TableOf(layout);
            var lights = PluginKeyNames.Lights(table);
            var first = lights.Where((_, i) => !table.Names[i].StartsWith("2nd ", StringComparison.Ordinal)).ToList();
            var faults = KeyTableOrderTests.Faults(first).ToList();
            Assert.True(faults.Count == 0, string.Join("; ", faults));
        }

        /// <summary>asusctl's g513i, g634j and g635l files list MediaNext in the Caps row and
        /// MediaPrev in the Shift row, and its g533q file the other way round. LED 100 is
        /// Previous and LED 121 Next on G-Helper (PRV15 and NXT15, app/USB/Aura.cs:572-575), on
        /// OpenRGB (AsusAuraCoreLaptopDevices.cpp:95 and 97, :121-122) and on RGB.NET
        /// (AsusKeyboardLedMapping.cs:225-226).</summary>
        [Theory]
        [InlineData("g513i-per-key")]
        [InlineData("g533q-per-key")]
        [InlineData("g634j-per-key")]
        [InlineData("g635l-per-key")]
        [InlineData("")]
        public void TheMediaKeysAtARowsEnd_AreNamedByTheirRow(string layout)
        {
            var lights = PluginKeyNames.Lights(TableOf(layout));
            Assert.Equal(LightLabel.Previous, lights.Single(l => l.Address == 100).Label);
            Assert.Equal(LightLabel.Next, lights.Single(l => l.Address == 121).Label);
        }

        /// <summary>Every name in a table finds a usage or a label, and a second LED's name
        /// counts as its key's.</summary>
        [Theory]
        [MemberData(nameof(PerKeyTables))]
        public void EveryName_IsAUsageOrALabel(string layout)
            => Assert.Empty(PluginKeyNames.Unresolved(TableOf(layout)));

        /// <summary>A second LED under a key that has no usage keeps that key's label or kind,
        /// as a second LED under a key with a usage keeps the usage.</summary>
        [Fact]
        public void ASecondLight_KeepsItsKeysLabelAndKind()
        {
            string[] names = { "Fn", "2nd Fn", "Logo", "2nd Logo", "Led Lid Left", "2nd Led Lid Left", "Up Arrow", "2nd Up Arrow" };
            var table = new PluginKeyTable("test", names, Enumerable.Range(0, names.Length).ToArray(), new byte[names.Length], new byte[names.Length]);
            var lights = PluginKeyNames.Lights(table);
            Assert.Equal(LightLabel.Fn, lights[1].Label);
            Assert.Equal(LightKind.Other, lights[1].Kind);
            Assert.Equal(LightKind.Logo, lights[3].Kind);
            Assert.Equal(LightKind.Edge, lights[5].Kind);
            Assert.Equal(Up, lights[7].Usage);
            Assert.Empty(PluginKeyNames.Unresolved(table));

            // A table of usages names only the lights no usage does, by the same rule.
            var byUsage = new PluginKeyTable("test", new[] { "2nd Fn" }, new[] { 0 }, new byte[1], new byte[1], new ushort[1]);
            Assert.Equal(LightLabel.Fn, PluginKeyNames.Lights(byUsage)[0].Label);
        }

        /// <summary>asusctl's fan and ROG keys carry the labels OpenRGB's path gives them.</summary>
        [Fact]
        public void TheFanAndRogKeys_HaveTheirLabels()
        {
            var lights = PluginKeyNames.Lights(TableOf("g634j-per-key"));
            Assert.Equal(LightLabel.Fan, lights.Single(l => l.Address == 5).Label);
            Assert.Equal(LightLabel.Rog, lights.Single(l => l.Address == 6).Label);
        }

        /// <summary>The G635L's lid is three LEDs on G-Helper (KSTN LOGO LIDL LIDR, 0, 167, 176,
        /// 177), on OpenRGB's lid layouts (AsusAuraCoreLaptopDevices.cpp:155-157 and 622-624)
        /// and in this script's default table. Its logo is 167.</summary>
        [Fact]
        public void TheG635lLid_HasItsLogoAt167_AndItsTwoSidesApart()
        {
            var lights = PluginKeyNames.Lights(TableOf("g635l-per-key"));
            Assert.Equal(LightKind.Logo, lights.Single(l => l.Address == 167).Kind);
            Assert.Equal(LightKind.Edge, lights.Single(l => l.Address == 176).Kind);
            Assert.Equal(LightKind.Edge, lights.Single(l => l.Address == 177).Kind);
            Assert.Single(lights, l => l.Kind == LightKind.Logo);
        }

        /// <summary>G-Helper paints both sets of arrow LEDs on every laptop (packetMap: ARWU
        /// ARWL ARWD ARWR at 139, 159, 160, 161, and ARW? at 120, 140, 141, 143), since a board
        /// has its arrows on one or the other and no list says which. Each set's LEDs reach
        /// their arrow's usage, except where the layout's own key holds the LED.</summary>
        public static IEnumerable<object[]> ArrowLeds()
        {
            foreach (object[] layout in PerKeyTables())
                foreach (var (led, usage) in new[]
                {
                    (139, Up), (159, Left), (160, Down), (161, Right), (120, Up), (140, Left), (141, Down), (143, Right),
                })
                    yield return new object[] { layout[0], led, usage };
        }

        [Theory]
        [MemberData(nameof(ArrowLeds))]
        public void EveryTable_NamesBothArrowSets(string layout, int led, ushort usage)
        {
            var table = TableOf(layout);
            var lights = PluginKeyNames.Lights(table);
            int at = Array.IndexOf(table.Indexes, led);
            Assert.True(at >= 0, $"LED {led} is in no light");
            // gx502 and gx531 give 120 to Right Shift's third LED, and the g814ji table has
            // its Left and Down one LED on from the second set.
            bool ownKey = lights[at].Usage == RightShift
                || (layout == "g814ji-per-key" && led == 141 && lights[at].Usage == Left);
            Assert.True(ownKey || lights[at].Usage == usage, $"LED {led}: usage {lights[at].Usage:X2}");
        }

        /// <summary>LED 142 is an arrow on the tables whose arrows OpenRGB puts there, and the
        /// key to the right of Up where the layout has one: Print Screen, as OpenRGB's G533ZW has
        /// it (AsusAuraCoreLaptopDevices.cpp:128), or Right Fn (asusctl's rgb_for_led_code).</summary>
        [Theory]
        [InlineData("g634j-per-key", Right)]
        [InlineData("g635l-per-key", Right)]
        [InlineData("g814ji-per-key", Down)]
        [InlineData("g513i-per-key", PrintScreen)]
        [InlineData("g533q-per-key", PrintScreen)]
        public void Led142_IsTheKeyItsLayoutPutsThere(string layout, ushort usage)
            => Assert.Equal(usage, PluginKeyNames.Lights(TableOf(layout)).Single(l => l.Address == 142).Usage);

        /// <summary>The keystone is LED 175 in OpenRGB, on the G533ZW and the G733QR
        /// (AsusAuraCoreLaptopDevices.cpp:129 and 662). G-Helper's KSTN column holds a 0, the
        /// mark of an empty place in its packetMap, and no table lights LED 0 for it.</summary>
        [Theory]
        [InlineData("g533q-per-key")]
        [InlineData("")]
        public void TheKeystone_IsLed175(string layout)
        {
            var lights = PluginKeyNames.Lights(TableOf(layout));
            Assert.Contains(lights, l => l.Address == 175);
            Assert.DoesNotContain(lights, l => l.Address == 0);
        }

        /// <summary>A color set on one arrow reaches every LED of that arrow, whichever set a
        /// board has its key on (LightRenderer.ColorOf goes by the usage), and leaves the others
        /// to the background.</summary>
        [Theory]
        [MemberData(nameof(PerKeyTables))]
        public void AKeyColor_LightsBothLightsOfAnArrow(string layout)
        {
            var map = AsusLaptopAura.PerKeyMap(0x19B6, TableOf(layout), false, false);
            const int color = 0x123456, background = 0x010203;
            var colors = LightRenderer.Render(map, background, new KeyFrame(new Dictionary<ushort, int> { [Left] = color }, null, null, null));
            var lit = map.Lights.Where((_, i) => colors[i] == color).ToList();
            Assert.Contains(lit, l => l.Address == 159);
            Assert.Contains(lit, l => l.Address == 140);
            Assert.All(lit, l => Assert.Equal(Left, l.Usage));
        }

        /// <summary>The key picker draws a table's lights at their places, so no two share one.</summary>
        [Theory]
        [MemberData(nameof(PerKeyTables))]
        public void EveryTable_GivesEveryLightItsOwnPlace(string layout)
        {
            var table = TableOf(layout);
            var places = table.X.Zip(table.Y, (x, y) => (x, y)).ToList();
            Assert.Equal(places.Count, places.Distinct().Count());
        }

        // ── The ACPI family ──

        /// <summary>G-Helper's second device (AsusACPI.cs:950-957), with the magic that
        /// does not store: tried when the first refuses, and kept once it takes a color.</summary>
        [Fact]
        public void TheSecondAcpiDevice_TakesTheColorTheFirstRefuses()
        {
            var atk = new ScriptedAtk(device => device == AsusTufLaptopLighting.RgbModeDevice2 ? 1 : 0);
            var session = new AsusTufLaptopLighting.Session(atk);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, null, new[] { 0x112233 }));
            Assert.Equal(2, atk.Devices.Count);
            Assert.Equal(AsusTufLaptopLighting.RgbModeDevice, atk.Devices[0]);
            Assert.Equal(AsusTufLaptopLighting.RgbModeDevice2, atk.Devices[1]);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, null, new[] { 0x445566 }));
            Assert.Equal(3, atk.Devices.Count);
            Assert.Equal(AsusTufLaptopLighting.RgbModeDevice2, atk.Devices[2]);
            Assert.Equal("5A 00 10 00 B3 00 11 22 33 EB 00 00", Hex(AsusTufLaptopLighting.ColorArguments(0x112233, AsusTufLaptopLighting.RgbModeDevice2)));

            var refused = new ScriptedAtk(_ => 0);
            Assert.Equal(HidLightingPaint.Failed, new AsusTufLaptopLighting.Session(refused).Paint(null, _ => { }, null, new[] { 1 }));
            Assert.Equal(2, refused.Devices.Count);
        }

        /// <summary>G-Helper's look: every TUF name with no check of the device, and the
        /// rest of its list where DSTS reports an RGB mode device.</summary>
        [Theory]
        [InlineData("ASUS TUF Gaming F15", 0, true)]
        [InlineData("TUF Dash F15", 0, true)]
        [InlineData("asus tuf gaming f15", 0, true)]
        [InlineData("TX Gaming A15", 0, true)]
        [InlineData("TX Air", 0, true)]
        [InlineData("Vivobook Pro 16X", 0, false)]
        [InlineData("Vivobook Pro 16X", 0x10000, true)]
        [InlineData("ASUS Zenbook Pro", 0x10001, true)]
        [InlineData("ProArt P16", 0xFFFF, false)]
        [InlineData("EXPERTBOOK B9", 0x10000, true)]
        [InlineData("ASUS V16", 0x10000, true)]
        [InlineData("ASUSLaptop X515", 0x10000, true)]
        [InlineData("ROG Strix G15", 0x10000, false)]
        public void TheAcpiLook_IsGHelpers(string product, int dsts, bool listed)
        {
            var atk = new ScriptedAtk(_ => dsts);
            var family = new AsusTufLaptopLighting(() => product, _ => true, () => atk);
            Assert.Equal(listed, family.Look().Count == 1);
        }

        private sealed class ScriptedAtk : IAtkAcpiChannel
        {
            private readonly Func<uint, int> _answer;
            public readonly List<uint> Devices = new();

            public ScriptedAtk(Func<uint, int> answer) => _answer = answer;

            public int? Call(uint method, byte[] arguments)
            {
                uint device = BitConverter.ToUInt32(arguments, 0);
                Devices.Add(device);
                return _answer(device);
            }

            public void Dispose() { }
        }
    }
}
