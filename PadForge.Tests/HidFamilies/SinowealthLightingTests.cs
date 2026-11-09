using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using S = PadForge.Common.Input.Peripherals.SinowealthLighting;
using M = PadForge.Common.Input.Peripherals.SinowealthMouseLighting;

namespace PadForge.Tests
{
    /// <summary>The Sinowealth families against OpenRGB's SinowealthController
    /// at the pinned commit (#557). Every expected byte is written out from
    /// OpenRGB, never from the builder under test.</summary>
    [Collection("PeripheralOutputStatics")]
    public class SinowealthLightingTests
    {
        private static string Hex(byte[] bytes, int count) => ReportLog.Hex(bytes, count);
        private static string Hex(byte[] bytes, int from, int count) => ReportLog.Hex(bytes, from, count);

        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { S.Instance };

        private static HidCollectionInfo Vendor(ushort pid, int iface, ushort usage, ushort feature, string col = "")
            => KeyboardFamilyRig.Collection(0x258A, pid, 0xFF00, usage, iface: iface, feature: feature, tail: col);

        // ─────────────────────────────────────────────
        //  The tables and the collections
        // ─────────────────────────────────────────────

        [Fact]
        public void TheTables_AreOpenRgbsAtThePinnedCommit()
        {
            Assert.Equal("b9d903b544a40486709c4ca097d0b234b617db9c", SinowealthLayouts.OpenRgbCommit);
            // SinowealthKeyboard90Controller.h:22-144: 104 lights, Escape
            // index 0x01 at the top left, the number pad's Enter last with
            // 0x72 at row 5, column 22.
            Assert.Equal(104, SinowealthLayouts.Thor300.Length);
            Assert.Equal(new SinowealthLed("Key: Escape", 0x01, 0, 0), SinowealthLayouts.Thor300[0]);
            Assert.Equal(new SinowealthLed("Key: F12", 0x0F, 0, 13), SinowealthLayouts.Thor300[12]);
            Assert.Equal(new SinowealthLed("Key: Number Pad Enter", 0x72, 5, 22), SinowealthLayouts.Thor300[103]);
            // RGBController_Sinowealth1007.cpp:14-23.
            Assert.Equal(new[] { "Top Left", "Middle Left", "Bottom Left", "Bottom Middle", "Bottom Right", "Middle Right", "Top Right" },
                SinowealthLayouts.ZetFuryPro);
        }

        [Fact]
        public void EveryRegistration_IsMatchedByTheFamilyThatLightsIt()
        {
            var registrations = OpenRgbRegistrations.Of(S.FamilyName).ToList();
            // SinowealthControllerDetect.cpp:445-455. The FL ESPORTS F11 and
            // the 0x0016 keyboard are commented out (lines 457-459).
            Assert.Equal(11, registrations.Count);
            foreach (var r in registrations)
            {
                int iface = r.Interface == -1 ? 1 : r.Interface;
                ushort usage = (ushort)(r.Usage == -1 ? 1 : r.Usage);
                ushort page = (ushort)r.UsagePage;
                var collection = KeyboardFamilyRig.Collection(0x258A, (ushort)r.ProductId, page, usage, iface: iface);
                switch (r.Detector)
                {
                    case S.ZetDetector:
                        Assert.Null(S.Instance.Match(collection));
                        Assert.Same(S.Instance, S.Instance.Match(collection with { FeatureReportLength = 59 }).Family);
                        break;
                    case S.Keyboard10cDetector:
                        Assert.Null(S.Instance.Match(collection));
                        Assert.Equal(r.Name, S.Instance.Match(collection with { FeatureReportLength = 520 }).Name);
                        break;
                    case S.GmowCableDetector:
                    case S.GmowDongleDetector:
                    case S.Thor300Detector:
                        Assert.Equal(r.Name, S.Instance.Match(collection).Name);
                        break;
                    case M.MouseDetector:
                    case M.XenonDetector:
                        // Lit by the two-collection family's own look.
                        Assert.Null(S.Instance.Match(collection with { FeatureReportLength = 520 }));
                        Assert.Null(S.Instance.Match(collection with { FeatureReportLength = 154 }));
                        Assert.Same(r, M.RegistrationOf(collection));
                        break;
                    default:
                        Assert.Fail("no family lights " + r.Detector);
                        break;
                }
            }
            Assert.Null(M.Instance.Match(Vendor(0x0036, 1, 1, 520)));
        }

        [Fact]
        public void TheZetAndTheXenon_ShareAProduct_AndAreToldApartByTheirReport()
        {
            // 0x1007 is both (SinowealthControllerDetect.cpp:42 and 46): the
            // ZET by report 0x04 of 59 bytes, the Xenon by 154.
            Assert.Equal("ZET Fury Pro", S.Instance.Match(Vendor(0x1007, 1, 1, 59)).Name);
            Assert.Null(S.Instance.Match(Vendor(0x1007, 1, 1, 154)));
            Assert.Equal(M.XenonDetector, M.RegistrationOf(Vendor(0x1007, 1, 1, 154)).Detector);
        }

        // ─────────────────────────────────────────────
        //  The frames
        // ─────────────────────────────────────────────

        [Fact]
        public void The10cModelQuery_AndColors_AreOpenRgbs()
        {
            // SinowealthControllerDetect.cpp:418.
            var query = S.ModelQuery();
            Assert.Equal(520, query.Length);
            Assert.Equal("06 82 01 00 01 00 06 00", Hex(query, 8));
            Assert.All(query.Skip(7), b => Assert.Equal(0, b));
            // SinowealthKeyboard10cController.cpp:69-80: 06 08 00 00 01 00
            // 7A 01, then each light's color at 8 + 3 x its LED index.
            var lights = new[]
            {
                new LightInfo(0, LightKind.Key, 0x29, LightKeySource.Generated),
                new LightInfo(5, LightKind.Key, 0x3E, LightKeySource.Generated),
            };
            var frame = S.Keyboard10cFrame(lights, new[] { 0x112233, 0xAABBCC });
            Assert.Equal(520, frame.Length);
            Assert.Equal("06 08 00 00 01 00 7A 01 11 22 33", Hex(frame, 11));
            Assert.Equal("AA BB CC", Hex(frame, 8 + 15, 3));
            Assert.Equal(0, frame[11]);
        }

        [Fact]
        public void The10cModels_AreOpenRgbsSevenRows()
        {
            // SinowealthKeyboard10cDevices.cpp:299-321.
            Assert.Equal("kreo_swarm75_layout", S.Keyboard10cModel(0xBE).Recipe);
            Assert.Equal("aula_f75_layout", S.Keyboard10cModel(0xCD).Recipe);
            Assert.Equal("aula_f99_layout", S.Keyboard10cModel(0xA4).Recipe);
            Assert.Equal("aula_f87_layout", S.Keyboard10cModel(0x0B).Recipe);
            Assert.Equal("leobog_hi75c_pro_layout", S.Keyboard10cModel(0xA3).Recipe);
            Assert.Equal("redragon_k686_eisa_pro", S.Keyboard10cModel(0x20).Recipe);
            Assert.Equal("redragon_k686_eisa_pro", S.Keyboard10cModel(0x05).Recipe);
            Assert.Null(S.Keyboard10cModel(0x00));
            foreach (byte id in new byte[] { 0xBE, 0xCD, 0xA4, 0x0B, 0xA3, 0x20, 0x05 })
            {
                var lights = S.Keyboard10cLights(S.Keyboard10cModel(id));
                Assert.NotEmpty(lights);
                // Every key has an LED index the colors report can carry, and
                // no two keys share one.
                Assert.All(lights, l => Assert.InRange(l.Address, 0, (520 - 8) / 3 - 1));
                Assert.Equal(lights.Count, lights.Select(l => l.Address).Distinct().Count());
                Assert.Contains(lights, l => l.Usage == 0x29);
            }
        }

        [Fact]
        public void TheThor300Picture_IsModeKeysAndCommit()
        {
            var lights = S.Thor300Lights();
            Assert.Equal(104, lights.Count);
            Assert.Equal(0x01, lights[0].Address);
            Assert.Equal(0x29, lights[0].Usage);
            Assert.Equal(LightLabel.Fn, lights.Single(l => l.Address == 0x7D).Label);
            var colors = new int[104];
            colors[0] = 0x102030;
            var frames = S.Thor300Frames(lights, colors);
            // RGBController_SinowealthKeyboard90.cpp:114-128 and
            // SinowealthKeyboard90Controller.cpp:59-119: 0A 03 01, the mode
            // 0A 0A 13 04 02 07, then 0A 0C 01 key R G B per light, then
            // the commit 0A 0C 01 89.
            Assert.Equal(107, frames.Count);
            Assert.All(frames, f => Assert.Equal(8, f.Length));
            Assert.Equal("0A 03 01 00 00 00 00 00", Hex(frames[0], 8));
            Assert.Equal("0A 0A 13 04 02 07 00 00", Hex(frames[1], 8));
            Assert.Equal("0A 0C 01 01 10 20 30 00", Hex(frames[2], 8));
            Assert.Equal("0A 0C 01 03 00 00 00 00", Hex(frames[3], 8));
            Assert.Equal("0A 0C 01 89 00 00 00 00", Hex(frames[106], 8));
        }

        [Fact]
        public void TheZetWrite_IsTheConfigurationWithCustomModeAndSevenColors()
        {
            var configuration = Enumerable.Range(0, 59).Select(i => (byte)(0x80 + i)).ToArray();
            configuration[0] = 0x04;
            var lights = S.ZetLights();
            Assert.Equal(Enumerable.Range(1, 7), lights.Select(l => l.Zone));
            var colors = new[] { 0x010203, 0, 0, 0, 0, 0, 0x0A0B0C };
            var frame = S.ZetFrame(configuration, lights, colors);
            // SinowealthController1007.cpp:100-112: byte 21 the mode
            // (0x60 + speed 2), 22 the direction (0x00), 23 on the colors.
            Assert.Equal(59, frame.Length);
            Assert.Equal("04 81 82", Hex(frame, 3));
            Assert.Equal("94 62 00 01 02 03 00", Hex(frame, 20, 7));
            Assert.Equal("0A 0B 0C", Hex(frame, 41, 3));
            Assert.Equal(configuration.Skip(44), frame.Skip(44));
        }

        [Fact]
        public void TheGmowStaticMode_IsThreeReports()
        {
            // SinowealthGMOWController.cpp:18-40 and 156-174: the mode
            // packet 00 00 00 02 05 02 00 01 FF 04 00 09 R G B, the wired
            // brightness 00 00 00 02 02 02 02 01 7F and the wireless 00.
            var frames = S.GmowFrames(0x112233);
            Assert.Equal(3, frames.Length);
            Assert.All(frames, f => Assert.Equal(65, f.Length));
            Assert.Equal("00 00 00 02 05 02 00 01 FF 04 00 09 11 22 33 00", Hex(frames[0], 16));
            Assert.Equal("00 00 00 02 02 02 02 01 7F 00", Hex(frames[1], 10));
            Assert.Equal("00 00 00 02 02 02 02 00 7F 00", Hex(frames[2], 10));
            // Black goes as red 1 (lines 165-168).
            Assert.Equal("01 00 00", Hex(S.GmowFrames(0)[0], 12, 3));
        }

        // ─────────────────────────────────────────────
        //  The sessions on the worker
        // ─────────────────────────────────────────────

        [Fact]
        public void A10cClaim_AsksTheModel_LightsByItsLayout_AndKeepsTheStreamAlive()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 520 };
            rig.Log.FeatureAnswer = (id, last) =>
            {
                if (id != 0x06 || last == null || last[1] != 0x82) return null;
                var answer = new byte[520];
                answer[0] = 0x06;
                answer[13] = 0xA4;
                return answer;
            };
            var collection = Vendor(0x010C, 1, 1, 520, "&col03");
            using var backend = rig.Lit(Families, collection, 0x112233);
            var sent = rig.Log.Sent();
            Assert.Equal("06 82 01 00 01 00 06", Hex(sent[0].Report, 7));
            Assert.Equal('G', sent[1].Kind);
            Assert.Equal("06 08 00 00 01 00 7A 01 11 22 33", Hex(sent[2].Report, 11));
            Assert.Contains(S.Keyboard10cWaitMs, rig.Waits);
            // The published map is the AULA F99's, by its model byte.
            var map = PeripheralLightMaps.For(KeyboardFamilyRig.PathOf(collection));
            Assert.Equal("sinowealth:258A:010C:A4", map.Product);
            Assert.Equal(S.Keyboard10cLights(S.Keyboard10cModel(0xA4)).Count, map.Lights.Count);
            // The same picture goes again after a second with nothing sent
            // (RGBController_SinowealthKeyboard10c.cpp:163 and 181-191).
            int frames = rig.Log.Of('F').Count;
            rig.Advance(999);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(frames, rig.Log.Of('F').Count);
            rig.Advance(1);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(frames + 1, rig.Log.Of('F').Count);
            Assert.Equal("06 08 00 00 01 00 7A 01 11 22 33", Hex(rig.Log.Of('F')[^1], 11));
            // Nothing for the crash path, and nothing at the hand-back.
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }

        [Fact]
        public void A10cModelOpenRgbDoesNotKnow_GetsNoSession()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 520 };
            rig.Log.FeatureAnswer = (id, _) => { var a = new byte[520]; a[0] = 6; a[13] = 0x77; return a; };
            var collection = Vendor(0x010C, 1, 1, 520);
            using var backend = rig.Lit(Families, collection);
            Assert.DoesNotContain(rig.Log.Of('F'), f => f[1] == 0x08);
            Assert.True(backend.Snapshot.Units.Single().Asleep);
        }

        [Fact]
        public void AZetClaim_BacksUpTheConfiguration_AndTheHandBackWritesItBack()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 59 };
            var stored = new byte[59];
            stored[0] = 0x04;
            stored[21] = 0x22;
            stored[22] = 0x80;
            stored[23] = 0x55;
            rig.Log.FeatureAnswer = (id, _) => id == 0x04 ? (byte[])stored.Clone() : null;
            var collection = Vendor(0x1007, 1, 1, 59);
            using var backend = rig.Lit(Families, collection, 0x010203, holdMs: S.StoredHoldMs);
            // Read at the start, read again before the write, then the write.
            var writes = rig.Log.Of('F');
            var write = Assert.Single(writes);
            Assert.Equal("62 00 01 02 03", Hex(write, 21, 5));
            string key = KeyboardProfileBackups.DeviceKey("sinowealth:258A:1007", collection.Path);
            Assert.Equal(stored, KeyboardProfileBackups.Get(key)?.Contents);
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(stored, Assert.Single(record.Frames));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Equal(stored, Assert.Single(rig.Log.Of('F')));
            Assert.Null(KeyboardProfileBackups.Get(key));
        }

        [Fact]
        public void AZetConfigurationOwed_IsWrittenBackWithNoClaim()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 59 };
            var owed = new byte[59];
            owed[0] = 0x04;
            owed[21] = 0x31;
            var collection = Vendor(0x1007, 1, 1, 59);
            string key = KeyboardProfileBackups.DeviceKey("sinowealth:258A:1007", collection.Path);
            KeyboardProfileBackups.Take(key, 0, owed);
            // The mouse still shows a picture of PadForge's.
            var ours = new byte[59];
            ours[0] = 0x04;
            ours[21] = S.ZetModeCustomDefault;
            ours[22] = S.ZetDirectionRight;
            rig.Log.FeatureAnswer = (id, _) => id == 0x04 ? (byte[])ours.Clone() : null;
            using var backend = rig.Started(Families, collection);
            rig.Advance(HidLightingBackend.RepairGraceMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(owed, Assert.Single(rig.Log.Of('F')));
            Assert.Null(KeyboardProfileBackups.Get(key));
        }

        [Fact]
        public void AThor300Claim_HoldsThePicture_AndHandsBackNothing()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 8 };
            var collection = Vendor(0x0090, 1, 1, 8);
            using var backend = rig.Started(Families, collection);
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(collection));
            KeyboardFamilyRig.Light(0xFF0000);
            KeyboardFamilyRig.Passes(backend);
            // Held: nothing yet.
            Assert.Empty(rig.Log.Sent());
            rig.Advance(S.StoredHoldMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(107, rig.Log.Of('F').Count);
            Assert.Equal("0A 0C 01 01 FF 00 00 00", Hex(rig.Log.Of('F')[2], 8));
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }

        [Fact]
        public void AGmowClaim_WaitsBeforeEachReport()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 65 };
            var collection = KeyboardFamilyRig.Collection(0x258A, 0x2011, 0xFFFF, 0x00, iface: 2, feature: 65);
            using var backend = rig.Lit(Families, collection, 0x445566, holdMs: S.StoredHoldMs);
            var frames = rig.Log.Of('F');
            Assert.Equal(3, frames.Count);
            Assert.Equal("44 55 66", Hex(frames[0], 12, 3));
            Assert.Equal(3, rig.Waits.Count(w => w == S.GmowWaitMs));
        }
    }

    /// <summary>The two-collection Sinowealth mice against OpenRGB (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class SinowealthMouseLightingTests
    {
        private static string Hex(byte[] bytes, int count) => ReportLog.Hex(bytes, count);
        private static string Hex(byte[] bytes, int from, int count) => ReportLog.Hex(bytes, from, count);

        private static readonly Guid Box = KeyboardFamilyRig.Box;

        private static HidCollectionInfo Col(ushort pid, int col, ushort feature)
            => KeyboardFamilyRig.Collection(0x258A, pid, 0xFF00, 0x0001, iface: 1, feature: feature, tail: $"&col{col:x2}");

        private static M Family(IReadOnlyList<HidCollectionInfo> walk, Func<string, IHidReportChannel> open = null,
            Func<string, Guid> container = null)
            => new(() => walk, container ?? (_ => Box), open ?? (_ => null), (_, _, _) => true);

        /// <summary>The pair a walk checks for a replug says its handles are dead
        /// when either is, for the two collections and for one that is both.</summary>
        [Fact]
        public void ThePair_SaysItIsGone_WhenEitherHandleIs()
        {
            var data = new FakeReportChannel();
            var command = new FakeReportChannel();
            object pair = new M.Channels(data, command);
            var connected = Assert.IsAssignableFrom<IConnectedChannel>(pair);
            Assert.True(connected.StillConnected);
            command.StillConnected = false;
            Assert.False(connected.StillConnected);
            command.StillConnected = true;
            data.StillConnected = false;
            Assert.False(connected.StillConnected);

            object one = new M.Channels(command, command);
            Assert.True(Assert.IsAssignableFrom<IConnectedChannel>(one).StillConnected);
        }

        /// <summary>Either collection opening with no access right leaves nothing
        /// open and says another program has it. A collection that did not open
        /// at all says nothing of the kind.</summary>
        [Theory]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        [InlineData(false, false, false)]
        public void AnOpen_SaysWhenACollectionOpenedWithNoAccessRight(bool dataHeld, bool commandHeld, bool held)
        {
            var opened = new List<FakeReportChannel>();
            IHidReportChannel Open(string path)
            {
                if (!dataHeld && !commandHeld) return null;
                var channel = new FakeReportChannel { Path = path, FeatureOnly = path == "data" ? dataHeld : commandHeld };
                opened.Add(channel);
                return channel;
            }
            var model = Family(Array.Empty<HidCollectionInfo>(), Open).Model("Mouse", 0x0036, false, "command");
            Assert.Null(model.Open("data"));
            Assert.Equal(held, model.OpenFoundHeld());
            Assert.All(opened, c => Assert.True(c.Disposed));
        }

        [Fact]
        public void TheLook_PairsTheDataAndCommandCollections_ByTheirReports()
        {
            // The Glorious Model O: report 0x04 of 520 and the 6-byte command
            // report, with three collections on page 0xFF00
            // (SinowealthControllerDetect.cpp:296-318).
            var walk = new[] { Col(0x0036, 1, 6), Col(0x0036, 2, 520), Col(0x0036, 3, 0) };
            var found = Assert.Single(Family(walk).Look());
            Assert.Equal(walk[1].Path, found.Key);
            Assert.Equal("Glorious Model O / O-", found.Model.Name);
            // Two collections are fewer than the detector wants.
            Assert.Empty(Family(walk.Take(2).ToArray()).Look());
            // A collection in no known container waits.
            Assert.Empty(Family(walk, container: _ => Guid.Empty).Look());
            // The Xenon 200: 154 and 9, five collections (lines 257-274).
            var xenon = new[] { Col(0x1007, 1, 154), Col(0x1007, 2, 9), Col(0x1007, 3, 0), Col(0x1007, 4, 0), Col(0x1007, 5, 0) };
            var x = Assert.Single(Family(xenon).Look());
            Assert.Equal("Genesis Xenon 200", x.Model.Name);
            Assert.Equal(xenon[0].Path, x.Key);
            // Without its commit collection it is not lit.
            Assert.Empty(Family(new[] { xenon[0], xenon[2], xenon[3], xenon[4], Col(0x1007, 6, 0) }).Look());
            // A ZET's collections are not the Xenon's.
            Assert.Empty(Family(new[] { Col(0x1007, 1, 59), Col(0x1007, 2, 9), xenon[2], xenon[3], xenon[4] }).Look());
        }

        [Fact]
        public void TheGloriousWrite_IsOpenRgbsStaticMode()
        {
            var configuration = new byte[520];
            for (int i = 0; i < 520; i++) configuration[i] = (byte)(i + 1);
            var write = M.GloriousWrite(configuration, 0x112233);
            // SinowealthController.cpp:100-121: 167 bytes copied, byte 3 =
            // 7B, byte 6 = 00, mode 02 at 0x35, brightness 2 << 4 at 0x38,
            // red, blue, green at 0x39.
            Assert.Equal(520, write.Length);
            Assert.Equal("01 02 03 7B 05 06 00 08", Hex(write, 8));
            Assert.Equal(0x02, write[0x35]);
            Assert.Equal(0x20, write[0x38]);
            Assert.Equal("11 33 22", Hex(write, 0x39, 3));
            Assert.Equal(configuration[166], write[166]);
            Assert.All(write.Skip(167), b => Assert.Equal(0, b));
            // A restore keeps the mouse's own mode and colors.
            var restore = M.GloriousWrite(configuration, null);
            Assert.Equal(configuration[0x35], restore[0x35]);
            Assert.Equal(configuration[0x39], restore[0x39]);
            Assert.Equal(0x7B, restore[3]);
        }

        [Fact]
        public void TheXenonWrite_IsOpenRgbsSaveMode()
        {
            var read = new byte[154];
            read[0] = 0x04;
            read[0x5E] = 0x77;
            var write = M.XenonWrite(read, 0xAABBCC);
            // GenesisXenon200Controller.cpp:45-49: 0x5D mode 0x18, 0x60 the
            // value 0x51, 0x61 red, green, blue.
            Assert.Equal("18 77 00 51 AA BB CC", Hex(write, 0x5D, 7));
            Assert.Equal(154, write.Length);
        }

        [Fact]
        public void AGloriousClaim_ReadsThroughBothCollections_AndRestoresAtTheHandBack()
        {
            var walk = new[] { Col(0x0033, 1, 6), Col(0x0033, 2, 520), Col(0x0033, 3, 0) };
            using var rig = new KeyboardFamilyRig { FeatureLength = 520 };
            var stored = new byte[520];
            stored[0] = 0x04;
            stored[0x35] = 0x05;
            stored[0x39] = 0x99;
            rig.Log.FeatureAnswer = (id, last) => id == 0x04 && last != null && last[0] == 0x05 ? (byte[])stored.Clone() : null;
            var opened = new List<string>();
            IHidReportChannel Open(string path)
            {
                lock (opened) opened.Add(path);
                return new ReportLogChannel { Path = path, Device = rig.Log, FeatureReportLength = path == walk[0].Path ? 6 : 520 };
            }
            var family = Family(walk, Open);
            var unit = KeyboardFamilyRig.Collection(0, 0, 0, 0) with { Path = walk[1].Path };
            using var backend = rig.Started(new IHidLightingFamily[] { family }, walk);
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(unit));
            KeyboardFamilyRig.Light(0x102030);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(SinowealthLighting.StoredHoldMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Contains(walk[0].Path, opened);
            Assert.Contains(walk[1].Path, opened);
            var writes = rig.Log.Of('F').Where(f => f[0] == 0x04).ToList();
            var write = Assert.Single(writes);
            Assert.Equal("10 30 20", Hex(write, 0x39, 3));
            Assert.Equal(0x02, write[0x35]);
            // Its firmware first (driver-sinowealth.c:1043-1056), and every read is
            // asked for with 05 11 (lines 190-211).
            var asked = rig.Log.Of('F').Where(f => f[0] == 0x05).ToList();
            Assert.Equal("05 01 00 00 00 00", Hex(asked[0], 6));
            Assert.All(asked.Skip(1), f => Assert.Equal("05 11 00 00 00 00", Hex(f, 6)));
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(0x05, Assert.Single(record.Frames)[0x35]);
            rig.Log.Clear();
            rig.Release(backend);
            // The hand-back reads the configuration first, with 05 11.
            var back = Assert.Single(rig.Log.Of('F'), f => f[0] == 0x04);
            Assert.Equal(0x05, back[0x35]);
            Assert.Equal(0x99, back[0x39]);
            Assert.Equal(0x7B, back[3]);
            Assert.Null(KeyboardProfileBackups.Get(KeyboardProfileBackups.DeviceKey("sinowealth:258A:0033", walk[1].Path)));
        }

        /// <summary>What a Glorious claim writes on a firmware: libratbag's table of which take red,
        /// green, blue (driver-sinowealth.c) and the V161 with no lights.</summary>
        private static List<byte[]> GloriousWrites(string firmware)
        {
            var walk = new[] { Col(0x0033, 1, 6), Col(0x0033, 2, 520), Col(0x0033, 3, 0) };
            using var rig = new KeyboardFamilyRig { FeatureLength = 520 };
            var stored = new byte[520];
            stored[0] = 0x04;
            stored[0x35] = 0x05;
            rig.Log.FeatureAnswer = (id, last) =>
            {
                if (id == 0x05 && last != null && last[1] == 0x01)
                    return new byte[] { 0x05, 0x01 }.Concat(System.Text.Encoding.ASCII.GetBytes(firmware)).ToArray();
                return id == 0x04 && last != null && last[0] == 0x05 ? (byte[])stored.Clone() : null;
            };
            IHidReportChannel Open(string path)
                => new ReportLogChannel { Path = path, Device = rig.Log, FeatureReportLength = path == walk[0].Path ? 6 : 520 };
            var family = Family(walk, Open);
            var unit = KeyboardFamilyRig.Collection(0, 0, 0, 0) with { Path = walk[1].Path };
            using var backend = rig.Started(new IHidLightingFamily[] { family }, walk);
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(unit));
            KeyboardFamilyRig.Light(0x102030);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(SinowealthLighting.StoredHoldMs);
            KeyboardFamilyRig.Passes(backend);
            return rig.Log.Of('F').Where(f => f[0] == 0x04).ToList();
        }

        [Theory]
        [InlineData("3106", "10 20 30")]
        [InlineData("V287", "10 20 30")]
        [InlineData("V102", "10 30 20")]
        public void AGloriousClaim_TakesTheOrderOfItsFirmware(string firmware, string written)
        {
            Assert.Equal(written, Hex(Assert.Single(GloriousWrites(firmware)), 0x39, 3));
        }

        [Fact]
        public void AFirmwareWithNoLights_GetsNoClaim()
            => Assert.Empty(GloriousWrites(M.UnlitFirmware));

        [Fact]
        public void AXenonClaim_CommitsThroughTheCommandCollection()
        {
            var walk = new[] { Col(0x1007, 1, 154), Col(0x1007, 2, 9), Col(0x1007, 3, 0), Col(0x1007, 4, 0), Col(0x1007, 5, 0) };
            using var rig = new KeyboardFamilyRig();
            var stored = new byte[154];
            stored[0] = 0x04;
            stored[0x5D] = 0x14;
            rig.Log.FeatureAnswer = (id, _) => id switch
            {
                0x04 => (byte[])stored.Clone(),
                0x08 => new byte[] { 0x08, 1, 2, 3, 4, 5, 6, 7, 8 },
                _ => null,
            };
            var family = Family(walk, path => new ReportLogChannel { Path = path, Device = rig.Log, FeatureReportLength = path == walk[1].Path ? 9 : 154 });
            var unit = KeyboardFamilyRig.Collection(0, 0, 0, 0) with { Path = walk[0].Path };
            using var backend = rig.Started(new IHidLightingFamily[] { family }, walk);
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(unit));
            KeyboardFamilyRig.Light(0x0A0B0C);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(SinowealthLighting.StoredHoldMs);
            KeyboardFamilyRig.Passes(backend);
            var features = rig.Log.Of('F');
            Assert.Equal(2, features.Count);
            Assert.Equal("18 00 00 51 0A 0B 0C", Hex(features[0], 0x5D, 7));
            Assert.Equal("08 01 02 03 04 05 06 07 08", Hex(features[1], 9));
            rig.Log.Clear();
            rig.Release(backend);
            features = rig.Log.Of('F');
            Assert.Equal(2, features.Count);
            Assert.Equal(stored, features[0]);
            Assert.Equal(9, features[1].Length);
        }
    }
}
