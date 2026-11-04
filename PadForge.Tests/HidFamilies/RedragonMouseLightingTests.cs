using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using R = PadForge.Common.Input.Peripherals.RedragonMouseLighting;

namespace PadForge.Tests
{
    /// <summary>The Redragon mice against OpenRGB's RedragonController at
    /// the pinned commit, and their hand-back against mouse_m908's reads
    /// (#557). Every expected byte is written out here from the
    /// references.</summary>
    [Collection("PeripheralOutputStatics")]
    public class RedragonMouseLightingTests
    {
        private static readonly IReadOnlyList<IHidLightingFamily> Families = new IHidLightingFamily[] { R.Instance };

        private static HidCollectionInfo Mouse(ushort pid = 0xFC30)
            => KeyboardFamilyRig.Collection(0x04D9, pid, 0xFFA0, 1, iface: 2, feature: 16);

        /// <summary>A mouse that answers mouse_m908's reads: feature report
        /// 2 after a read request, the value from byte 8.</summary>
        private static Func<byte, byte[], byte[]> Answers(byte profile, params byte[] record) => (id, last) =>
        {
            if (id != 0x02 || last == null || last[1] != 0xF2) return null;
            var answer = new byte[16];
            answer[0] = 0x02;
            if (last[2] == 0x2C && last[3] == 0x00) answer[8] = profile;
            else if (last[2] == 0x49 && last[3] == 0x04) record.CopyTo(answer, 8);
            else return null;
            return answer;
        };

        private const string Fc30 = "redragon:04D9:FC30";

        [Fact]
        public void EveryRegistration_IsMatched_OnInterface2Page0xFFA0()
        {
            var registrations = OpenRgbRegistrations.Of(R.OpenRgbFamily).ToList();
            // RedragonControllerDetect.cpp:55-65.
            Assert.Equal(11, registrations.Count);
            foreach (var r in registrations)
            {
                var collection = CollectionFor(r, feature: 16);
                var model = R.Instance.Match(collection);
                Assert.True(model != null, r.Name);
                Assert.Equal(r.Name, model.Name);
                Assert.Same(model, HidLightingFamilies.Match(collection));
                Assert.Equal($"redragon:04D9:{r.ProductId:X4}", model.Map.Product);
                // Every mode is saved by the mouse (RGBController_RedragonMouse.cpp:39-69).
                Assert.Equal(LenovoLegionLighting.HoldMs, model.HoldMs);
                Assert.Equal(LenovoLegionLighting.RepaintMs, model.RepaintMs);
                Assert.Empty(model.Programs);
                var light = Assert.Single(model.Map.Lights);
                Assert.Equal(LightKind.Zone, light.Kind);
                Assert.Null(R.Instance.Match(collection with { FeatureReportLength = 15 }));
                Assert.Null(R.Instance.Match(collection with { UsagePage = 0xFF00 }));
            }
        }

        [Fact]
        public void TheReports_AreOpenRgbsWritesAndApplies_AndMouseM908sReads()
        {
            // RedragonMouseController.cpp:22-25 and 87-133.
            var enter = R.EnterFrames(0x112233);
            Assert.Equal(4, enter.Length);
            Assert.Equal("02 F3 2C 00 01 00 00 00 00 00 00 00 00 00 00 00", Hex(enter[0]));
            Assert.Equal("02 F1 02 04 00 00 00 00 00 00 00 00 00 00 00 00", Hex(enter[1]));
            Assert.Equal("02 F3 49 04 06 00 00 00 11 22 33 01 00 02 00 00", Hex(enter[2]));
            Assert.Equal(Hex(enter[1]), Hex(enter[3]));
            // Lines 56-70 and 98-106.
            var color = R.ColorFrames(0xA0B0C0);
            Assert.Equal(2, color.Length);
            Assert.Equal("02 F3 49 04 03 00 00 00 A0 B0 C0 00 00 00 00 00", Hex(color[0]));
            Assert.Equal(Hex(enter[1]), Hex(color[1]));
            // mouse_m908 include/m711/data.cpp:294-296 and 777.
            Assert.Equal("02 F5 00 00 00 00 00 00 00 00 00 00 00 00 00 00", Hex(R.Bracket(false)));
            Assert.Equal("02 F5 01 00 00 00 00 00 00 00 00 00 00 00 00 00", Hex(R.Bracket(true)));
            Assert.Equal("02 F2 2C 00 01 00 00 00 00 00 00 00 00 00 00 00", Hex(R.ReadRequest(0x002C, 1)));
            Assert.Equal("02 F2 49 04 07 00 00 00 00 00 00 00 00 00 00 00", Hex(R.ReadRequest(0x0449, 7)));
            // The hand-back: the record's six bytes, applied, then the profile, applied.
            var restore = R.RestoreFrames(0x03, new byte[] { 1, 2, 3, 4, 5, 6 });
            Assert.Equal("02 F3 49 04 06 00 00 00 01 02 03 04 05 06 00 00", Hex(restore[0]));
            Assert.Equal(Hex(enter[1]), Hex(restore[1]));
            Assert.Equal("02 F3 2C 00 01 00 00 00 03 00 00 00 00 00 00 00", Hex(restore[2]));
            Assert.Equal(Hex(enter[1]), Hex(restore[3]));
        }

        [Fact]
        public void AClaim_ReadsBracketed_BacksUp_EntersStatic_ThenWritesTheColorAlone_AndHandsBack()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 16 };
            rig.Log.FeatureAnswer = Answers(0x02, 0xFF, 0x00, 0x00, 0x01, 0x05, 0x08, 0x03);
            var collection = Mouse();
            using var backend = rig.Lit(Families, collection, 0x102030, holdMs: R.HoldMs);
            var sent = rig.Log.Sent();
            // F5 00, read the profile, read the record, F5 01.
            Assert.Equal("02 F5 00", Hex(sent[0].Report, 3));
            Assert.Equal("02 F2 2C 00 01", Hex(sent[1].Report, 5));
            Assert.Equal('G', sent[2].Kind);
            Assert.Equal("02 F2 49 04 07", Hex(sent[3].Report, 5));
            Assert.Equal('G', sent[4].Kind);
            Assert.Equal("02 F5 01", Hex(sent[5].Report, 3));
            var backup = KeyboardProfileBackups.Get(Fc30);
            Assert.NotNull(backup);
            Assert.Equal(0x02, backup.Value.Profile);
            Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0x01, 0x05, 0x08 }, backup.Value.Contents);
            var writes = rig.Log.Of('F').Skip(4).ToList();
            Assert.Equal(R.EnterFrames(0x102030).Select(f => Hex(f)), writes.Select(f => Hex(f)));
            var record = Assert.Single(backend.Recorded);
            Assert.Equal(R.RestoreFrames(0x02, backup.Value.Contents).Select(f => Hex(f)), record.Frames.Select(f => Hex(f)));
            // A new color: held, then the color alone.
            rig.Log.Clear();
            KeyboardFamilyRig.Light(0x405060);
            KeyboardFamilyRig.Passes(backend);
            Assert.Empty(rig.Log.Sent());
            rig.Advance(R.RepaintMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(R.ColorFrames(0x405060).Select(f => Hex(f)), rig.Log.Of('F').Select(f => Hex(f)));
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Equal(R.RestoreFrames(0x02, new byte[] { 0xFF, 0x00, 0x00, 0x01, 0x05, 0x08 }).Select(f => Hex(f)),
                rig.Log.Of('F').Select(f => Hex(f)));
            Assert.Null(KeyboardProfileBackups.Get(Fc30));
        }

        [Fact]
        public void AMouseThatDoesNotAnswerTheRead_IsLitWithNothingToHandBack()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 16 };
            var collection = Mouse(0xFC4D);
            using var backend = rig.Lit(Families, collection, 0x00FF00, holdMs: R.HoldMs);
            var sent = rig.Log.Sent();
            // The bracket opens, the first read fails, the bracket closes.
            Assert.Equal("02 F5 00", Hex(sent[0].Report, 3));
            Assert.Equal("02 F2 2C 00 01", Hex(sent[1].Report, 5));
            Assert.Equal('G', sent[2].Kind);
            Assert.Equal("02 F5 01", Hex(sent[3].Report, 3));
            Assert.Equal(R.EnterFrames(0x00FF00).Select(f => Hex(f)), rig.Log.Of('F').Skip(3).Select(f => Hex(f)));
            Assert.Null(KeyboardProfileBackups.Get("redragon:04D9:FC4D"));
            Assert.Empty(backend.Recorded);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Empty(rig.Log.Sent());
        }

        [Fact]
        public void ABackupOwed_IsTheOriginal_AndIsWrittenBackWithNoClaim()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 16 };
            var owed = new byte[] { 0x01, 0x02, 0x03, 0x01, 0x00, 0x08 };
            KeyboardProfileBackups.Take(Fc30, 0x04, owed);
            using var backend = rig.Started(Families, Mouse());
            rig.Advance(HidLightingBackend.RepairGraceMs);
            KeyboardFamilyRig.Passes(backend);
            Assert.Equal(R.RestoreFrames(0x04, owed).Select(f => Hex(f)), rig.Log.Of('F').Select(f => Hex(f)));
            Assert.Null(KeyboardProfileBackups.Get(Fc30));
        }

        [Fact]
        public void ASessionWithABackupOwed_ReadsNothing_AndHandsBackTheBackup()
        {
            var owed = new byte[] { 0x09, 0x08, 0x07, 0x01, 0x02, 0x04 };
            KeyboardProfileBackups.ResetForTest(persistAtOnce: true);
            try
            {
                KeyboardProfileBackups.Take(Fc30, 0x01, owed);
                var io = new FakeReportChannel { FeatureReportLength = 16 };
                var session = (R.Session)R.Instance.Match(Mouse()).Begin(io, _ => { });
                Assert.Empty(io.Calls);
                Assert.Equal(owed, session.Record);
                Assert.Equal(0x01, session.Profile);
                Assert.True(session.HandBack(io, _ => { }));
                Assert.Equal(R.RestoreFrames(0x01, owed).Select(f => Hex(f)), Calls(io).Select(c => Hex(c.Bytes)));
                Assert.Null(KeyboardProfileBackups.Get(Fc30));
            }
            finally
            {
                KeyboardProfileBackups.ResetForTest();
            }
        }

        [Fact]
        public void APictureWaitsForTheBackupToReachTheSettings()
        {
            KeyboardProfileBackups.ResetForTest(persistAtOnce: false);
            try
            {
                var io = new FakeReportChannel { FeatureReportLength = 16 };
                io.Feature = id => id == 0x02 ? new byte[16] : null;
                var model = R.Instance.Match(Mouse());
                var session = model.Begin(io, _ => { });
                int before = io.Calls.Count;
                Assert.Equal(HidLightingPaint.Later, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x123456 }));
                Assert.Equal(R.SavingMs, session.LaterMs);
                Assert.Equal(before, io.Calls.Count);
                Assert.NotNull(KeyboardProfileBackups.Get(Fc30));
            }
            finally
            {
                KeyboardProfileBackups.ResetForTest();
            }
        }
    }
}
