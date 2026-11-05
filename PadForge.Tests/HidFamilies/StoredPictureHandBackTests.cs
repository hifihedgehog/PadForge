using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>What a session or a later run writes back to a device whose
    /// pictures are stored, or that a killed run left in host control: only
    /// what PadForge moved, only while the device still shows PadForge's
    /// picture, and from a backup that reached the settings file before the
    /// first write.</summary>
    [Collection("PeripheralOutputStatics")]
    public class StoredPictureHandBackTests : IDisposable
    {
        public StoredPictureHandBackTests() => KeyboardProfileBackups.ResetForTest(persistAtOnce: true);

        public void Dispose() => KeyboardProfileBackups.ResetForTest();

        private static void Saved()
        {
            KeyboardProfileBackups.SaveRegistry(out int version);
            KeyboardProfileBackups.Persisted(version);
        }

        private static int Held() => KeyboardProfileBackups.SaveRegistry(out _).Count;

        private static List<byte[]> Sets(FakeReportChannel io)
            => io.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).ToList();

        // ── Roccat Kova and Sense Aimo ──

        private const string KovaKey = "roccat:1E7D:2CEE";
        private const string SenseKey = "roccat:1E7D:343A";

        private static HidLightingModel Roccat(string name)
            => RoccatLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of(RoccatLighting.FamilyName).Single(r => r.Name == name)));

        /// <summary>A profile report as the mouse answers it, its checksum
        /// right. Byte 6 sits in the DPI fields OpenRGB's header names
        /// (RoccatKovaController.h).</summary>
        private static byte[] KovaBytes(byte mode, byte flags = 0x41, byte dpi = 0x10, byte wheelRed = 0)
        {
            var report = new byte[RoccatLighting.KovaProfileLength];
            report[0] = RoccatLighting.KovaProfileReport;
            report[1] = RoccatLighting.KovaProfileLength;
            report[6] = dpi;
            report[RoccatLighting.KovaFlags] = flags;
            report[RoccatLighting.KovaMode] = mode;
            report[RoccatLighting.KovaWheel + 1] = wheelRed;
            int sum = report.Take(26).Sum(b => b);
            report[RoccatLighting.KovaChecksum] = (byte)sum;
            report[RoccatLighting.KovaChecksum + 1] = (byte)(sum >> 8);
            return report;
        }

        private static FakeReportChannel Kova(Func<byte[]> read) => new()
        {
            Path = null,
            FeatureReportLength = RoccatLighting.KovaProfileLength,
            Feature = id => id == RoccatLighting.KovaProfileReport ? read() : new byte[8],
        };

        [Fact]
        public void AKovaSwarmChangedSince_KeepsItsProfile_AtTheRepair()
        {
            var backup = KovaBytes(mode: 0x02, wheelRed: 0x33);
            KeyboardProfileBackups.Take(KovaKey, 0, backup);
            var swarms = KovaBytes(mode: 0x05, flags: 0x00, dpi: 0x20);
            var io = Kova(() => swarms);
            Assert.True(Roccat("Roccat Kova").Repair(io, _ => { }));
            Assert.DoesNotContain(Sets(io), s => s[0] == RoccatLighting.KovaProfileReport);
            Assert.Null(KeyboardProfileBackups.Get(KovaKey));
        }

        [Fact]
        public void AKovaThatHoldsTheBackupAlready_IsNotWritten_AtTheRepair()
        {
            var backup = KovaBytes(mode: 0x02);
            KeyboardProfileBackups.Take(KovaKey, 0, backup);
            var io = Kova(() => backup);
            Assert.True(Roccat("Roccat Kova").Repair(io, _ => { }));
            Assert.DoesNotContain(Sets(io), s => s[0] == RoccatLighting.KovaProfileReport);
            Assert.Null(KeyboardProfileBackups.Get(KovaKey));
        }

        [Fact]
        public void AKovaStillOnPadForgesPicture_GetsItsLightingBack_AndKeepsItsDpi()
        {
            var backup = KovaBytes(mode: 0x02, wheelRed: 0x33);
            KeyboardProfileBackups.Take(KovaKey, 0, backup);
            var ours = RoccatLighting.KovaProfile(KovaBytes(mode: 0x02, dpi: 0x7A), 0x112233, 0x445566);
            var io = Kova(() => ours);
            Assert.True(Roccat("Roccat Kova").Repair(io, _ => { }));
            var written = Sets(io).Last();
            Assert.Equal(0x02, written[RoccatLighting.KovaMode]);
            Assert.Equal(0x33, written[RoccatLighting.KovaWheel + 1]);
            Assert.Equal(0x7A, written[6]);
            int sum = written.Take(26).Sum(b => b);
            Assert.Equal((byte)sum, written[RoccatLighting.KovaChecksum]);
            Assert.Null(KeyboardProfileBackups.Get(KovaKey));
        }

        [Fact]
        public void ASessionOverAProfileSwarmChanged_TakesThatProfile_AndDropsTheBackup()
        {
            KeyboardProfileBackups.Take(KovaKey, 0, KovaBytes(mode: 0x02));
            var swarms = KovaBytes(mode: 0x05, flags: 0x00, dpi: 0x20);
            var session = Assert.IsType<RoccatLighting.StoredSession>(Roccat("Roccat Kova").Begin(Kova(() => swarms), _ => { }));
            Assert.Equal(swarms, session.Original);
            Assert.Null(KeyboardProfileBackups.Get(KovaKey));
        }

        [Fact]
        public void AKovaHandBack_KeepsADpiChangedDuringTheSession()
        {
            var now = KovaBytes(mode: 0x02, wheelRed: 0x33);
            var io = Kova(() => now);
            var model = Roccat("Roccat Kova");
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x112233, 0x445566 }));
            now = RoccatLighting.KovaProfile(KovaBytes(mode: 0x02, dpi: 0x7A), 0x112233, 0x445566);
            Assert.True(session.HandBack(io, _ => { }));
            var written = Sets(io).Last();
            Assert.Equal(0x7A, written[6]);
            Assert.Equal(0x02, written[RoccatLighting.KovaMode]);
            Assert.Equal(0x33, written[RoccatLighting.KovaWheel + 1]);
        }

        /// <summary>A claim can end before a session's first picture, and the
        /// backup a killed run left must not go with nothing written.</summary>
        [Fact]
        public void AKovaSessionOverAKilledRunsPicture_HandsBack_EvenWithNoPictureOfItsOwn()
        {
            var user = KovaBytes(mode: 0x02, wheelRed: 0x33);
            KeyboardProfileBackups.Take(KovaKey, 0, user);
            var ours = RoccatLighting.KovaProfile(KovaBytes(mode: 0x02), 0x112233, 0x445566);
            var io = Kova(() => ours);
            var session = Roccat("Roccat Kova").Begin(io, _ => { });
            Assert.NotEmpty(session.CrashFrames);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(0x02, Sets(io).Last()[RoccatLighting.KovaMode]);
            Assert.Equal(0x33, Sets(io).Last()[RoccatLighting.KovaWheel + 1]);
            Assert.Null(KeyboardProfileBackups.Get(KovaKey));
        }

        private static byte[] SenseBytes(byte mode)
        {
            var report = new byte[RoccatLighting.SenseModeLength];
            report[0] = RoccatLighting.SenseModeReport;
            report[1] = 0x01;
            report[2] = mode;
            report[10] = mode;
            return report;
        }

        private static FakeReportChannel Sense(byte[] answer) => new()
        {
            Path = null,
            FeatureReportLength = RoccatLighting.SenseModeLength,
            Feature = id => id == RoccatLighting.SenseModeReport ? answer : null,
        };

        [Fact]
        public void ASenseAimoSwarmChangedSince_KeepsItsMode_AtTheRepair()
        {
            KeyboardProfileBackups.Take(SenseKey, 1, SenseBytes(0x0A));
            var io = Sense(SenseBytes(0x05));
            Assert.True(Roccat("Roccat Sense Aimo Mid").Repair(io, _ => { }));
            Assert.Empty(Sets(io));
            Assert.Null(KeyboardProfileBackups.Get(SenseKey));
        }

        [Fact]
        public void ASenseAimoStillInDirect_GetsItsModeBack_AtTheRepair()
        {
            KeyboardProfileBackups.Take(SenseKey, 1, SenseBytes(0x0A));
            var io = Sense(SenseBytes(RoccatLighting.SenseDirect));
            Assert.True(Roccat("Roccat Sense Aimo Mid").Repair(io, _ => { }));
            Assert.Equal(SenseBytes(0x0A), Assert.Single(Sets(io)));
            Assert.Null(KeyboardProfileBackups.Get(SenseKey));
        }

        // ── Roccat Kone EMP ──

        private const string EmpKey = "roccat:kone-emp";

        private static byte[] EmpBytes(byte dpi = 2, byte led0Red = 0x44)
        {
            var settings = new byte[RoccatKoneEmpLighting.SettingsLength];
            settings[0] = 0x06;
            settings[1] = 0x29;
            settings[2] = 0x01;
            settings[17] = dpi;
            settings[19] = 0xF0;
            settings[20] = 0x01;
            settings[21] = 0x03;
            settings[23] = 0x05;
            settings[24] = led0Red;
            RoccatKoneEmpLighting.PutChecksum(settings);
            return settings;
        }

        private static FakeReportChannel Emp(Func<byte[]> settings)
        {
            var io = new FakeReportChannel { Path = null, FeatureReportLength = 41 };
            io.Feature = id => id switch
            {
                0x04 => new byte[] { 0x04, 0x01 },
                0x05 => new byte[] { 0x05, 0x03, 0x02 },
                0x06 => settings(),
                _ => null,
            };
            return io;
        }

        private static HidLightingModel EmpModel()
            => RoccatKoneEmpLighting.Instance.Match(KeyboardFamilyRig.Collection(0x1E7D, 0x2E24, 0xFF00, 1, iface: 0, feature: 41));

        private static readonly int[] EmpColors = { 0x112233, 0x445566, 0x778899, 0xAABBCC };

        /// <summary>A backup of the profile <see cref="Emp"/> answers as active.</summary>
        private static byte[] EmpBackup(byte[] settings)
            => RoccatKoneEmpLighting.BackupOf(new Dictionary<byte, byte[]> { [2] = settings });

        [Fact]
        public void TheKoneEmp_SavesItsSettings_BeforeItsFirstPicture()
        {
            KeyboardProfileBackups.ResetForTest();
            var settings = EmpBytes();
            var io = Emp(() => settings);
            var model = EmpModel();
            var session = model.Begin(io, _ => { });
            int before = Sets(io).Count;
            Assert.Equal(HidLightingPaint.Later, session.Paint(io, _ => { }, model.Map.Lights, EmpColors));
            Assert.Equal(before, Sets(io).Count);
            Assert.Empty(session.CrashFrames);
            Assert.Equal(EmpBackup(settings), KeyboardProfileBackups.Get(EmpKey)?.Contents);
            Saved();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, EmpColors));
            Assert.Equal(settings, Assert.Single(session.CrashFrames));
        }

        [Fact]
        public void AKoneEmpLitAndUnplugged_HandsBackWhatItsProfileHeld()
        {
            var settings = EmpBytes();
            var now = settings;
            var io = Emp(() => now);
            var model = EmpModel();
            var first = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, first.Paint(io, _ => { }, model.Map.Lights, EmpColors));
            // No hand-back, and the mouse keeps the picture.
            now = RoccatKoneEmpLighting.Written(settings, model.Map.Lights, EmpColors);
            var second = model.Begin(io, _ => { });
            Assert.NotEmpty(second.CrashFrames);
            Assert.True(second.HandBack(io, _ => { }));
            Assert.Equal(0x44, Sets(io).Last()[24]);
            Assert.Equal(0x05, Sets(io).Last()[23]);
            Assert.Null(KeyboardProfileBackups.Get(EmpKey));
        }

        [Fact]
        public void TheKoneEmpCrashFrames_AreOneArray_WhileTheyHold()
        {
            var settings = EmpBytes();
            var io = Emp(() => settings);
            var model = EmpModel();
            var session = model.Begin(io, _ => { });
            session.Paint(io, _ => { }, model.Map.Lights, EmpColors);
            Assert.Same(session.CrashFrames, session.CrashFrames);
        }

        [Fact]
        public void WhatAKilledRunLeftOnAKoneEmp_GoesBack_OrIsLeftWhenSomethingElseSetIt()
        {
            var model = EmpModel();
            Assert.NotNull(model.Repair);
            var settings = EmpBytes();
            KeyboardProfileBackups.Take(EmpKey, 1, EmpBackup(settings));
            var ours = RoccatKoneEmpLighting.Written(EmpBytes(dpi: 4), model.Map.Lights, EmpColors);
            var io = Emp(() => ours);
            Assert.True(model.Repair(io, _ => { }));
            var written = Sets(io).Last();
            Assert.Equal(0x44, written[24]);
            Assert.Equal(4, written[17]);
            Assert.Null(KeyboardProfileBackups.Get(EmpKey));

            KeyboardProfileBackups.Take(EmpKey, 1, EmpBackup(settings));
            var swarms = EmpBytes(led0Red: 0x99);
            var other = Emp(() => swarms);
            Assert.True(model.Repair(other, _ => { }));
            Assert.DoesNotContain(Sets(other), s => s[0] == 0x06);
            Assert.Null(KeyboardProfileBackups.Get(EmpKey));
        }

        [Fact]
        public void TheKoneEmp_KeepsADpiChangedDuringTheSession()
        {
            var settings = EmpBytes();
            var now = settings;
            var io = Emp(() => now);
            var model = EmpModel();
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, EmpColors));
            now = RoccatKoneEmpLighting.Written(EmpBytes(dpi: 4), model.Map.Lights, EmpColors);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, EmpColors));
            Assert.Equal(4, Sets(io).Last()[17]);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(4, Sets(io).Last()[17]);
            Assert.Equal(0x44, Sets(io).Last()[24]);
        }

        /// <summary>A Kone EMP as libratbag drives it: report 05 names the
        /// active profile, 04 with a profile and 80 picks the settings report
        /// 06 answers with, and a written report 06 lands on the profile its
        /// byte 2 names, with neither of those changing the active one
        /// (roccat_current_profile, roccat_set_config_profile,
        /// roccat_write_profile).</summary>
        private sealed class EmpMouse : IHidReportChannel
        {
            public readonly Dictionary<byte, byte[]> Profiles = new();
            public byte Active;
            public int Writes;
            private byte _picked;

            public EmpMouse(byte active, params byte[] profiles)
            {
                Active = active;
                foreach (byte id in profiles)
                {
                    var settings = EmpBytes(led0Red: (byte)(0x40 + id));
                    settings[2] = id;
                    RoccatKoneEmpLighting.PutChecksum(settings);
                    Profiles[id] = settings;
                }
            }

            public byte[] Original(byte id)
            {
                var settings = EmpBytes(led0Red: (byte)(0x40 + id));
                settings[2] = id;
                RoccatKoneEmpLighting.PutChecksum(settings);
                return settings;
            }

            public string Path => null;
            public bool FeatureOnly => false;
            public int InputReportLength => 0;
            public int OutputReportLength => 0;
            public int FeatureReportLength => 41;

            public bool SetFeature(byte[] report)
            {
                if (report[0] == 0x04) _picked = report[1];
                if (report[0] == 0x06)
                {
                    Profiles[report[2]] = (byte[])report.Clone();
                    Writes++;
                }
                return true;
            }

            public bool GetFeature(byte reportId, byte[] buffer)
            {
                Array.Clear(buffer);
                byte[] answer = reportId switch
                {
                    0x04 => new byte[] { 0x04, 0x01 },
                    0x05 => new byte[] { 0x05, 0x03, Active },
                    0x06 => Profiles[_picked],
                    _ => null,
                };
                if (answer == null) return false;
                Array.Copy(answer, buffer, Math.Min(answer.Length, buffer.Length));
                return true;
            }

            public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs) => false;
            public int ReadInput(byte[] buffer, int timeoutMs) => -1;
            public void Dispose() { }
        }

        /// <summary>The one backup held the first profile's bytes, and the
        /// hand-back wrote them over the second while the first kept
        /// PadForge's color.</summary>
        [Fact]
        public void AProfileSwitchDuringASession_HandsBackEveryProfileItWrote()
        {
            var mouse = new EmpMouse(2, 2, 3);
            var model = EmpModel();
            var session = model.Begin(mouse, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors));
            mouse.Active = 3;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors));
            Assert.True(RoccatKoneEmpLighting.LooksOurs(mouse.Profiles[2]));
            Assert.True(RoccatKoneEmpLighting.LooksOurs(mouse.Profiles[3]));

            Assert.True(session.HandBack(mouse, _ => { }));
            Assert.Equal(mouse.Original(2), mouse.Profiles[2]);
            Assert.Equal(mouse.Original(3), mouse.Profiles[3]);
            Assert.Equal(3, mouse.Active);
            Assert.Equal(0, Held());
        }

        [Fact]
        public void ASecondProfile_IsBackedUp_BeforeItsFirstWrite()
        {
            KeyboardProfileBackups.ResetForTest();
            var mouse = new EmpMouse(2, 2, 3);
            var model = EmpModel();
            var session = model.Begin(mouse, _ => { });
            Assert.Equal(HidLightingPaint.Later, session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors));
            Saved();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors));
            int writes = mouse.Writes;
            mouse.Active = 3;
            Assert.Equal(HidLightingPaint.Later, session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors));
            Assert.Equal(writes, mouse.Writes);
            Assert.Equal(mouse.Original(3), mouse.Profiles[3]);
            Saved();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors));
            Assert.True(RoccatKoneEmpLighting.LooksOurs(mouse.Profiles[3]));
        }

        [Fact]
        public void TheCrashPath_PutsBackEveryProfileASessionWrote()
        {
            var mouse = new EmpMouse(2, 2, 3);
            var model = EmpModel();
            var session = model.Begin(mouse, _ => { });
            session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors);
            Assert.Equal(mouse.Original(2), Assert.Single(session.CrashFrames));
            mouse.Active = 3;
            session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors);
            var frames = session.CrashFrames;
            Assert.Equal(2, frames.Length);
            Assert.Contains(frames, f => f.AsSpan().SequenceEqual(mouse.Original(2)));
            Assert.Contains(frames, f => f.AsSpan().SequenceEqual(mouse.Original(3)));
            session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors);
            Assert.Same(frames, session.CrashFrames);
        }

        [Fact]
        public void WhatAKilledRunLeftOnTwoProfiles_GoesBack_AtTheRepair()
        {
            var mouse = new EmpMouse(2, 2, 3);
            var model = EmpModel();
            var session = model.Begin(mouse, _ => { });
            session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors);
            mouse.Active = 3;
            session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors);
            Assert.True(model.Repair(mouse, _ => { }));
            Assert.Equal(mouse.Original(2), mouse.Profiles[2]);
            Assert.Equal(mouse.Original(3), mouse.Profiles[3]);
            Assert.Equal(3, mouse.Active);
            Assert.Equal(0, Held());
        }

        [Fact]
        public void TheNextSession_HandsBackEveryProfileAKilledRunLeft()
        {
            var mouse = new EmpMouse(2, 2, 3);
            var model = EmpModel();
            var first = model.Begin(mouse, _ => { });
            first.Paint(mouse, _ => { }, model.Map.Lights, EmpColors);
            mouse.Active = 3;
            first.Paint(mouse, _ => { }, model.Map.Lights, EmpColors);
            var next = model.Begin(mouse, _ => { });
            Assert.Equal(2, next.CrashFrames.Length);
            Assert.True(next.HandBack(mouse, _ => { }));
            Assert.Equal(mouse.Original(2), mouse.Profiles[2]);
            Assert.Equal(mouse.Original(3), mouse.Profiles[3]);
            Assert.Equal(0, Held());
        }

        [Fact]
        public void AProfileSwarmSetSince_KeepsWhatItHolds_AndTheOtherGoesBack()
        {
            var mouse = new EmpMouse(2, 2, 3);
            var model = EmpModel();
            var session = model.Begin(mouse, _ => { });
            session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors);
            mouse.Active = 3;
            session.Paint(mouse, _ => { }, model.Map.Lights, EmpColors);
            var swarms = EmpBytes(led0Red: 0x99);
            swarms[2] = 2;
            RoccatKoneEmpLighting.PutChecksum(swarms);
            mouse.Profiles[2] = swarms;
            Assert.True(model.Repair(mouse, _ => { }));
            Assert.Equal(swarms, mouse.Profiles[2]);
            Assert.Equal(mouse.Original(3), mouse.Profiles[3]);
            Assert.Equal(0, Held());
        }

        /// <summary>The Omen's table holds colors and nothing else, so no read
        /// tells PadForge's from ones the Hub stored, and with no claim behind
        /// it a write could undo the user's own.</summary>
        [Fact]
        public void TheOmenKeyboard_HasNoRepairWithNoClaim()
        {
            var model = Omen(new FakeHpBios()).Model;
            Assert.Null(model.Owed);
            Assert.Null(model.RepairOwn);
        }

        // ── HP Omen ──

        private const string OmenKey = "hp:omen-4zone";

        private static HpOmenLaptopLighting Omen(FakeHpBios bios) => new(() => true, () => bios, (_, _, _, _) => true);

        [Fact]
        public void AnOmenSessionAfterAFailedOne_HandsBackTheFirstTable()
        {
            var bios = new FakeHpBios();
            var user = (byte[])bios.Table.Clone();
            var family = Omen(bios);
            var model = family.Model;
            var first = model.BeginOwn(bios, _ => { });
            Assert.Equal(HidLightingPaint.Sent, first.Paint(null, _ => { }, model.Map.Lights, new[] { 1, 2, 3, 4 }));
            // The worker drops a session whose call failed, with no hand-back.
            var second = model.BeginOwn(bios, _ => { });
            Assert.Equal(HidLightingPaint.Sent, second.Paint(null, _ => { }, model.Map.Lights, new[] { 5, 6, 7, 8 }));
            Assert.True(second.HandBack(null, _ => { }));
            Assert.Equal(user, bios.Table);
            Assert.Null(KeyboardProfileBackups.Get(OmenKey));
        }

        [Fact]
        public void AnOmenPicture_WaitsForTheTableToBeSaved()
        {
            KeyboardProfileBackups.ResetForTest();
            var bios = new FakeHpBios();
            var user = (byte[])bios.Table.Clone();
            var model = Omen(bios).Model;
            var session = model.BeginOwn(bios, _ => { });
            Assert.Equal(HidLightingPaint.Later, session.Paint(null, _ => { }, model.Map.Lights, new[] { 1, 2, 3, 4 }));
            Assert.Equal(user, bios.Table);
            Assert.Equal(user, KeyboardProfileBackups.Get(OmenKey)?.Contents);
            Saved();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(null, _ => { }, model.Map.Lights, new[] { 1, 2, 3, 4 }));
        }

        // ── Lenovo Legion ──

        private const string LegionKey = "lenovo:048D:C197";
        private const int ExtraKey = 0x00B5;

        private static HidLightingModel Legion()
            => LenovoLegionLighting.Instance.Match(KeyboardFamilyRig.Collection(0x048D, 0xC197, 0xFF89, 0x07, feature: 960));

        private static (FakeSpectrumKeyboard Keyboard, FakeSpectrumChannel Io) Spectrum()
        {
            var keyboard = new FakeSpectrumKeyboard { Active = 5 };
            keyboard.OutsideCodes.Add(ExtraKey);
            return (keyboard, new FakeSpectrumChannel { Keyboard = keyboard });
        }

        /// <summary>A picture on a key no table lists read as another
        /// program's, and the hand-back let the only copy of the profile
        /// go.</summary>
        [Fact]
        public void AHandBack_WritesTheProfileBack_OverAPictureOnAKeyNoTableLists()
        {
            var (keyboard, io) = Spectrum();
            var model = Legion();
            Assert.DoesNotContain(model.Map.Lights, l => l.Address == ExtraKey);
            var user = keyboard.Stored(5);
            var session = model.Begin(io, _ => { });
            var lights = session.Map.Lights;
            Assert.Contains(lights, l => l.Address == ExtraKey);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new int[lights.Count]));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(user.Skip(2), keyboard.Stored(5).Skip(2));
            Assert.Null(KeyboardProfileBackups.Get(LegionKey));
        }

        [Fact]
        public void TheNextSession_KnowsAPictureOnAKeyNoTableLists_AsPadForges()
        {
            var (keyboard, io) = Spectrum();
            var model = Legion();
            var user = keyboard.Stored(5);
            var first = model.Begin(io, _ => { });
            var lights = first.Map.Lights;
            first.Paint(io, _ => { }, lights, new int[lights.Count]);
            // Killed: no hand-back. The next run's session.
            var second = Assert.IsType<LenovoLegionLighting.Session>(model.Begin(io, _ => { }));
            Assert.NotEmpty(second.CrashFrames);
            Assert.NotNull(KeyboardProfileBackups.Get(LegionKey));
            Assert.True(second.HandBack(io, _ => { }));
            Assert.Equal(user.Skip(2), keyboard.Stored(5).Skip(2));
        }

        [Fact]
        public void TheRepair_PutsTheProfileBack_OverAPictureOnAKeyNoTableLists()
        {
            var (keyboard, io) = Spectrum();
            var model = Legion();
            var user = keyboard.Stored(5);
            var first = model.Begin(io, _ => { });
            var lights = first.Map.Lights;
            first.Paint(io, _ => { }, lights, new int[lights.Count]);
            Assert.True(model.Repair(io, _ => { }));
            Assert.Equal(user.Skip(2), keyboard.Stored(5).Skip(2));
            Assert.Null(KeyboardProfileBackups.Get(LegionKey));
        }

        /// <summary>A write back that did not land leaves the picture on a
        /// key no table lists in the profile, so the backup stays until a
        /// write lands.</summary>
        [Fact]
        public void ARestoreThatDidNotLand_KeepsTheBackup_OverAPictureOnAKeyNoTableLists()
        {
            var (keyboard, io) = Spectrum();
            var model = Legion();
            var user = keyboard.Stored(5);
            var session = model.Begin(io, _ => { });
            var lights = session.Map.Lights;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, new int[lights.Count]));
            keyboard.LosesSaves = true;
            Assert.False(session.HandBack(io, _ => { }));
            Assert.NotNull(KeyboardProfileBackups.Get(LegionKey));
            keyboard.LosesSaves = false;
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(user.Skip(2), keyboard.Stored(5).Skip(2));
            Assert.Null(KeyboardProfileBackups.Get(LegionKey));
        }

        /// <summary>A key map that does not answer proves nothing, so the
        /// backup stays for a later try.</summary>
        [Fact]
        public void ARepairWhoseKeyMapDoesNotAnswer_KeepsTheBackup()
        {
            var (keyboard, io) = Spectrum();
            var model = Legion();
            var first = model.Begin(io, _ => { });
            var lights = first.Map.Lights;
            first.Paint(io, _ => { }, lights, new int[lights.Count]);
            keyboard.KeyCodes = null;
            Assert.False(model.Repair(io, _ => { }));
            Assert.NotNull(KeyboardProfileBackups.Get(LegionKey));
        }

        /// <summary>A Gen 7 to 9 stream the keyboard left for another profile
        /// and came back to shows the black group it stored. The same picture
        /// has to go out again, which enters the stream again.</summary>
        [Fact]
        public void AStream_AsksForItsPictureAgain_WhenTheKeyboardComesBack()
        {
            var keyboard = new FakeSpectrumKeyboard { Active = 5 };
            var io = new FakeSpectrumChannel { Keyboard = keyboard };
            var model = LenovoLegionLighting.Instance.Match(KeyboardFamilyRig.Collection(0x048D, 0xC977, 0xFF89, 0x07, feature: 960));
            var session = model.Begin(io, _ => { });
            var lights = session.Map?.Lights ?? model.Map.Lights;
            var colors = new int[lights.Count];
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, colors));
            Assert.False(session.RepaintDue);
            Assert.True(session.Recheck(io, _ => { }));
            Assert.False(session.RepaintDue);

            keyboard.Active = 4;
            Assert.True(session.Recheck(io, _ => { }));
            Assert.False(session.RepaintDue);
            keyboard.Active = 5;
            Assert.True(session.Recheck(io, _ => { }));
            Assert.True(session.RepaintDue);

            int before = keyboard.Calls().Count(c => c.StartsWith("D0"));
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, colors));
            Assert.Equal(before + 1, keyboard.Calls().Count(c => c.StartsWith("D0")));
            Assert.False(session.RepaintDue);
        }

        // ── A failed try does not lock a device out ──

        /// <summary>Three failed tries in a row stopped every try for the rest
        /// of the run. What failed them can pass, so the count clears a minute
        /// on.</summary>
        [Fact]
        public void ADeviceWhoseTriesFailed_IsTriedAgainAMinuteOn()
        {
            long now = 1000;
            KeyboardProfileBackups.ResetForTest(persistAtOnce: true, ticks: () => now);
            var model = Legion();
            var keyboard = new FakeSpectrumKeyboard { Active = 5 };
            var io = new FakeSpectrumChannel { Keyboard = keyboard };
            KeyboardProfileBackups.Take(LegionKey, 4, keyboard.Stored(4).Skip(2).ToArray());
            for (int i = 0; i < LenovoLegionLighting.RepairTries; i++) KeyboardProfileBackups.NoteFailure(LegionKey);
            Assert.False(model.Owed(null));
            Assert.Null(model.Begin(io, _ => { }));

            now += KeyboardProfileBackups.FailuresClearMs - 1;
            Assert.False(model.Owed(null));
            now += 1;
            Assert.True(model.Owed(null));
            Assert.Equal(0, KeyboardProfileBackups.Failures(LegionKey));
            Assert.NotNull(model.Begin(io, _ => { }));
        }

        // ── Redragon ──

        private static FakeReportChannel Redragon(byte profile, params byte[] record)
        {
            var io = new FakeReportChannel { Path = null, FeatureReportLength = 16 };
            io.Feature = id =>
            {
                var last = Sets(io).LastOrDefault();
                if (id != 0x02 || last == null || last[1] != 0xF2) return null;
                var answer = new byte[16];
                answer[0] = 0x02;
                if (last[2] == 0x2C && last[3] == 0x00) answer[8] = profile;
                else if (last[2] == 0x49 && last[3] == 0x04) record.CopyTo(answer, 8);
                return answer;
            };
            return io;
        }

        [Fact]
        public void ARedragonRecordOfZeros_IsNoRecord()
        {
            var session = Assert.IsType<RedragonMouseLighting.Session>(
                RedragonMouseLighting.Model("M711", 0xFC30).Begin(Redragon(0x01, new byte[7]), _ => { }));
            Assert.Null(session.Record);
        }

        [Fact]
        public void ARedragonProfileNoMouseHas_IsNoRecord()
        {
            var session = Assert.IsType<RedragonMouseLighting.Session>(
                RedragonMouseLighting.Model("M711", 0xFC30).Begin(Redragon(0x05, 0xFF, 0, 0, 1, 5, 8, 3), _ => { }));
            Assert.Null(session.Record);
        }

        /// <summary>mouse_m908 reads the M711, M715, M908 and M607, and no
        /// source reads the other seven of OpenRGB's models.</summary>
        [Theory]
        [InlineData(0xFC30, true)]
        [InlineData(0xFC39, true)]
        [InlineData(0xFC4D, true)]
        [InlineData(0xFC38, true)]
        [InlineData(0xFC3A, false)]
        [InlineData(0xFC5F, false)]
        [InlineData(0xFC58, false)]
        [InlineData(0xFA7E, false)]
        [InlineData(0xFC69, false)]
        [InlineData(0xFC40, false)]
        [InlineData(0xFC62, false)]
        public void OnlyTheModelsASourceReads_AreAsked(int productId, bool read)
        {
            var io = Redragon(0x01, 0xFF, 0, 0, 1, 5, 8, 3);
            var session = Assert.IsType<RedragonMouseLighting.Session>(
                RedragonMouseLighting.Model("Redragon", (ushort)productId).Begin(io, _ => { }));
            Assert.Equal(read, session.Record != null);
            Assert.Equal(read, Sets(io).Any(s => s[1] == 0xF2));
        }

        // ── Sinowealth ──

        private static HidCollectionInfo Col(ushort pid, int col, ushort feature, ushort vendor = 0x258A)
            => KeyboardFamilyRig.Collection(vendor, pid, 0xFF00, 0x0001, iface: 1, feature: feature, tail: $"&col{col:x2}");

        /// <summary>A Glorious-report mouse on the worker, its firmware and
        /// its configuration scripted, claimed once. The rig is left lit.</summary>
        private static (KeyboardFamilyRig Rig, HidLightingBackend Backend, HidCollectionInfo Unit) LitGlorious(
            ushort pid, string firmware, byte[] stored, ushort vendor = 0x258A)
        {
            var walk = new[] { Col(pid, 1, 6, vendor), Col(pid, 2, 520, vendor), Col(pid, 3, 0, vendor) };
            var rig = new KeyboardFamilyRig { FeatureLength = 520 };
            rig.Log.FeatureAnswer = (id, last) =>
            {
                if (id == 0x05 && last != null && last[1] == 0x01)
                    return new byte[] { 0x05, 0x01 }.Concat(System.Text.Encoding.ASCII.GetBytes(firmware)).ToArray();
                return id == 0x04 && last != null && last[0] == 0x05 ? (byte[])stored.Clone() : null;
            };
            IHidReportChannel Open(string path)
                => new ReportLogChannel { Path = path, Device = rig.Log, FeatureReportLength = path == walk[0].Path ? 6 : 520 };
            var family = new SinowealthMouseLighting(() => walk, _ => KeyboardFamilyRig.Box, Open, (_, _, _) => true);
            var unit = KeyboardFamilyRig.Collection(0, 0, 0, 0) with { Path = walk[1].Path };
            var backend = rig.Started(new IHidLightingFamily[] { family }, walk);
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(unit));
            KeyboardFamilyRig.Light(0x102030);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(SinowealthLighting.StoredHoldMs);
            KeyboardFamilyRig.Passes(backend);
            return (rig, backend, unit);
        }

        private static byte[] GloriousConfig(byte effect = 0x05)
        {
            var stored = new byte[520];
            stored[0] = 0x04;
            stored[0x07] = 0x03;
            stored[0x35] = effect;
            stored[0x38] = 0x40;
            stored[0x39] = 0x99;
            return stored;
        }

        [Theory]
        [InlineData(0x0051, "2642", 0x258A, true)]
        [InlineData(0x0051, "3106", 0x258A, false)]
        [InlineData(0x0051, "XXXX", 0x258A, false)]
        [InlineData(0x0027, "3106", 0x258A, true)]
        [InlineData(0x0027, "3110", 0x258A, true)]
        [InlineData(0x0027, "V102", 0x258A, true)]
        [InlineData(0x0027, "V161", 0x258A, false)]
        [InlineData(0x0027, "1234", 0x258A, false)]
        [InlineData(0xA000, "1009", 0x3794, true)]
        [InlineData(0xA000, "3106", 0x3794, false)]
        public void AMouseOnlyLibratbagLists_IsLitOnTheFirmwareItsDeviceFileNames(int pid, string firmware, int vendor, bool lit)
        {
            var (rig, backend, _) = LitGlorious((ushort)pid, firmware, GloriousConfig(), (ushort)vendor);
            using (rig)
            using (backend)
                Assert.Equal(lit, rig.Log.Of('F').Any(f => f[0] == 0x04));
        }

        /// <summary>"The value mice with no LEDs have ... Do **not** overwrite
        /// it" (libratbag driver-sinowealth.c:191-195).</summary>
        [Fact]
        public void AMouseOnlyLibratbagLists_WhoseEffectByteSaysNoLeds_IsNotWritten()
        {
            var (rig, backend, _) = LitGlorious(0x0051, "2642", GloriousConfig(effect: 0xFF));
            using (rig)
            using (backend)
                Assert.DoesNotContain(rig.Log.Of('F'), f => f[0] == 0x04);
        }

        [Fact]
        public void AGloriousHandBack_PutsBackItsLighting_AndKeepsADpiChangedDuringTheSession()
        {
            var stored = GloriousConfig();
            var (rig, backend, _) = LitGlorious(0x0033, "3106", stored);
            using (rig)
            using (backend)
            {
                Assert.Single(rig.Log.Of('F'), f => f[0] == 0x04);
                stored[0x07] = 0x07;
                rig.Log.Clear();
                rig.Release(backend);
                var back = rig.Log.Of('F').Single(f => f[0] == 0x04);
                Assert.Equal(0x07, back[0x07]);
                Assert.Equal(0x05, back[0x35]);
                Assert.Equal(0x40, back[0x38]);
                Assert.Equal(0x99, back[0x39]);
                Assert.Equal(0x7B, back[3]);
            }
        }

        /// <summary>A Glorious-report mouse on the worker with no claim on it,
        /// and a backup a killed run left under its key.</summary>
        private static (KeyboardFamilyRig Rig, HidLightingBackend Backend) UnclaimedGlorious(
            ushort pid, Func<byte[]> read, byte[] backup)
        {
            var walk = new[] { Col(pid, 1, 6), Col(pid, 2, 520), Col(pid, 3, 0) };
            var rig = new KeyboardFamilyRig { FeatureLength = 520 };
            rig.Log.FeatureAnswer = (id, last) => id == 0x04 && last != null && last[0] == 0x05 ? read() : null;
            KeyboardProfileBackups.Take(KeyboardProfileBackups.DeviceKey(SinowealthLighting.Product(pid), walk[1].Path), 0, backup);
            IHidReportChannel Open(string path)
                => new ReportLogChannel { Path = path, Device = rig.Log, FeatureReportLength = path == walk[0].Path ? 6 : 520 };
            var family = new SinowealthMouseLighting(() => walk, _ => KeyboardFamilyRig.Box, Open, (_, _, _) => true);
            var backend = rig.Started(new IHidLightingFamily[] { family }, walk);
            rig.Advance(HidLightingBackend.RepairGraceMs);
            KeyboardFamilyRig.Passes(backend);
            return (rig, backend);
        }

        private static byte[] GloriousPicture(byte dpi = 0x03)
        {
            var ours = GloriousConfig(effect: SinowealthMouseLighting.GloriousStatic);
            ours[0x07] = dpi;
            ours[0x38] = (SinowealthMouseLighting.GloriousBrightnessNormal & 0xF) << 4;
            ours[0x39] = 0x11;
            return ours;
        }

        /// <summary>The worker skipped a family whose devices it opens through
        /// the family's own channel, so a killed run's stored picture stayed
        /// until the next claim.</summary>
        [Fact]
        public void WhatAKilledRunLeftOnAGloriousMouse_GoesBack_WithNoClaim()
        {
            var backup = GloriousConfig().Take(SinowealthMouseLighting.ConfigSize).ToArray();
            var ours = GloriousPicture(dpi: 0x07);
            var (rig, backend) = UnclaimedGlorious(0x0033, () => (byte[])ours.Clone(), backup);
            using (rig)
            using (backend)
            {
                var back = Assert.Single(rig.Log.Of('F'), f => f[0] == 0x04);
                Assert.Equal(0x05, back[0x35]);
                Assert.Equal(0x99, back[0x39]);
                Assert.Equal(0x07, back[0x07]);
                Assert.Equal(0x7B, back[3]);
                Assert.Equal(0, Held());
            }
        }

        [Fact]
        public void AGloriousMouseSomethingElseSetSince_KeepsWhatItHolds_WithNoClaim()
        {
            var backup = GloriousConfig().Take(SinowealthMouseLighting.ConfigSize).ToArray();
            var other = GloriousConfig(effect: 0x03);
            var (rig, backend) = UnclaimedGlorious(0x0033, () => (byte[])other.Clone(), backup);
            using (rig)
            using (backend)
            {
                Assert.DoesNotContain(rig.Log.Of('F'), f => f[0] == 0x04);
                Assert.Equal(1, rig.Log.Of('F').Count(f => f[0] == 0x05));
                Assert.Equal(0, Held());
            }
        }

        [Fact]
        public void AGloriousMouseThatDoesNotAnswer_StaysOwed_WithNoClaim()
        {
            var backup = GloriousConfig().Take(SinowealthMouseLighting.ConfigSize).ToArray();
            var (rig, backend) = UnclaimedGlorious(0x0033, () => null, backup);
            using (rig)
            using (backend)
            {
                Assert.DoesNotContain(rig.Log.Of('F'), f => f[0] == 0x04);
                Assert.Equal(1, Held());
                string key = KeyboardProfileBackups.DeviceKey(SinowealthLighting.Product(0x0033), Col(0x0033, 2, 520).Path);
                Assert.Equal(1, KeyboardProfileBackups.Failures(key));
            }
        }

        [Fact]
        public void WhatAKilledRunLeftOnAXenon_GoesBack_AndIsCommitted_WithNoClaim()
        {
            HidCollectionInfo X(int col, ushort feature) => Col(0x1007, col, feature);
            var walk = new[] { X(1, 154), X(2, 9), X(3, 0), X(4, 0), X(5, 0) };
            using var rig = new KeyboardFamilyRig { FeatureLength = 154 };
            var backup = new byte[SinowealthMouseLighting.XenonReportSize];
            backup[0] = 0x04;
            backup[0x5D] = 0x03;
            backup[0x61] = 0x77;
            var ours = SinowealthMouseLighting.XenonWrite(backup, 0x112233);
            ours[0x10] = 0x09;
            rig.Log.FeatureAnswer = (id, _) => id == 0x04 ? (byte[])ours.Clone() : id == 0x08 ? new byte[] { 0x08, 0, 0, 0, 0, 0, 0, 0, 0 } : null;
            KeyboardProfileBackups.Take(KeyboardProfileBackups.DeviceKey(SinowealthLighting.Product(0x1007), walk[0].Path), 0, backup);
            IHidReportChannel Open(string path)
                => new ReportLogChannel { Path = path, Device = rig.Log, FeatureReportLength = path == walk[1].Path ? 9 : 154 };
            var family = new SinowealthMouseLighting(() => walk, _ => KeyboardFamilyRig.Box, Open, (_, _, _) => true);
            using var backend = rig.Started(new IHidLightingFamily[] { family }, walk);
            rig.Advance(HidLightingBackend.RepairGraceMs);
            KeyboardFamilyRig.Passes(backend);
            var back = Assert.Single(rig.Log.Of('F'), f => f[0] == 0x04);
            Assert.Equal(0x03, back[0x5D]);
            Assert.Equal(0x77, back[0x61]);
            Assert.Equal(0x09, back[0x10]);
            Assert.Contains(rig.Log.Of('F'), f => f[0] == 0x08);
            Assert.Equal(0, Held());
        }

        [Fact]
        public void AZetHandBack_PutsBackItsLighting_AndKeepsADpiChangedDuringTheSession()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 59 };
            var stored = new byte[59];
            stored[0] = 0x04;
            stored[0x05] = 0x03;
            stored[21] = 0x22;
            stored[23] = 0x55;
            rig.Log.FeatureAnswer = (id, _) => id == 0x04 ? (byte[])stored.Clone() : null;
            var collection = KeyboardFamilyRig.Collection(0x258A, 0x1007, 0xFF00, 1, iface: 1, feature: 59);
            using var backend = rig.Lit(new IHidLightingFamily[] { SinowealthLighting.Instance }, collection, 0x010203, holdMs: SinowealthLighting.StoredHoldMs);
            stored[0x05] = 0x07;
            rig.Log.Clear();
            rig.Release(backend);
            var back = Assert.Single(rig.Log.Of('F'));
            Assert.Equal(0x07, back[0x05]);
            Assert.Equal(0x22, back[21]);
            Assert.Equal(0x55, back[23]);
        }

        [Theory]
        [InlineData(0x2011, 0x00)]
        [InlineData(0x2012, 0x00)]
        [InlineData(0x2022, 0x01)]
        [InlineData(0x2023, 0x00)]
        public void TheWirelessGloriousMice_AreLeftToGloriousCore(int pid, int usage)
        {
            Assert.Contains(VendorPrograms.GloriousCore,
                SinowealthLighting.Instance.Match(KeyboardFamilyRig.Collection(0x258A, (ushort)pid, 0xFFFF, (ushort)usage, iface: 2, feature: 65)).Programs);
        }

        [Fact]
        public void TheModelOEternal_IsLeftToGloriousCore_AndTheFirstWiredModelOIsNot()
        {
            Assert.Contains(VendorPrograms.GloriousCore,
                new SinowealthMouseLighting(() => Array.Empty<HidCollectionInfo>(), _ => Guid.Empty, _ => null, (_, _, _) => true)
                    .Model("Glorious Model O Eternal", 0xA000, false, "c").Programs);
            Assert.Empty(new SinowealthMouseLighting(() => Array.Empty<HidCollectionInfo>(), _ => Guid.Empty, _ => null, (_, _, _) => true)
                .Model("Glorious Model O", 0x0036, false, "c").Programs);
        }

        /// <summary>A channel onto a <see cref="ReportLog"/> that reports how
        /// many bytes its reads gave.</summary>
        private sealed class CountedChannel : IHidReportChannel
        {
            private readonly ReportLogChannel _inner;
            private readonly int _count;

            public CountedChannel(ReportLogChannel inner, int count)
            {
                _inner = inner;
                _count = count;
            }

            public string Path => _inner.Path;
            public bool FeatureOnly => _inner.FeatureOnly;
            public int InputReportLength => _inner.InputReportLength;
            public int OutputReportLength => _inner.OutputReportLength;
            public int FeatureReportLength => _inner.FeatureReportLength;
            public bool SetFeature(byte[] report) => _inner.SetFeature(report);
            public bool GetFeature(byte reportId, byte[] buffer) => _inner.GetFeature(reportId, buffer);
            public int GetFeatureCounted(byte reportId, byte[] buffer) => _inner.GetFeature(reportId, buffer) ? _count : -1;
            public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs) => _inner.WriteOutput(report, timeoutMs);
            public int ReadInput(byte[] buffer, int timeoutMs) => _inner.ReadInput(buffer, timeoutMs);
            public void Dispose() => _inner.Dispose();
        }

        private static List<byte[]> WritesAtCount(ushort pid, string firmware, int count)
        {
            var walk = new[] { Col(pid, 1, 6), Col(pid, 2, 520), Col(pid, 3, 0) };
            using var rig = new KeyboardFamilyRig { FeatureLength = 520 };
            var stored = GloriousConfig();
            rig.Log.FeatureAnswer = (id, last) =>
            {
                if (id == 0x05 && last != null && last[1] == 0x01)
                    return new byte[] { 0x05, 0x01 }.Concat(System.Text.Encoding.ASCII.GetBytes(firmware)).ToArray();
                return id == 0x04 && last != null && last[0] == 0x05 ? (byte[])stored.Clone() : null;
            };
            IHidReportChannel Open(string path) => new CountedChannel(
                new ReportLogChannel { Path = path, Device = rig.Log, FeatureReportLength = path == walk[0].Path ? 6 : 520 }, count);
            var family = new SinowealthMouseLighting(() => walk, _ => KeyboardFamilyRig.Box, Open, (_, _, _) => true);
            var unit = KeyboardFamilyRig.Collection(0, 0, 0, 0) with { Path = walk[1].Path };
            using var backend = rig.Started(new IHidLightingFamily[] { family }, walk);
            KeyboardFamilyRig.Link(KeyboardFamilyRig.PathOf(unit));
            KeyboardFamilyRig.Light(0x102030);
            KeyboardFamilyRig.Passes(backend);
            rig.Advance(SinowealthLighting.StoredHoldMs);
            KeyboardFamilyRig.Passes(backend);
            return rig.Log.Of('F').Where(f => f[0] == 0x04).ToList();
        }

        /// <summary>SetMode writes nothing below 131 bytes
        /// (SinowealthController.cpp:100), libratbag nothing below 123
        /// (driver-sinowealth.c:70, 1183-1186).</summary>
        [Theory]
        [InlineData(0x0033, "3106", 130, false)]
        [InlineData(0x0033, "3106", 131, true)]
        [InlineData(0x0051, "2642", 122, false)]
        [InlineData(0x0051, "2642", 123, true)]
        public void AGloriousRead_ShorterThanItsReferencesFloor_WritesNothing(int pid, string firmware, int count, bool lit)
        {
            Assert.Equal(lit, WritesAtCount((ushort)pid, firmware, count).Count > 0);
        }

        /// <summary>The length byte: 0x7B on the mice OpenRGB lists, and the
        /// size the read gave less 8 on the ones only libratbag lists
        /// (driver-sinowealth.c:1953).</summary>
        [Theory]
        [InlineData(0x0033, "3106", 131, 0x7B)]
        [InlineData(0x0033, "3106", 167, 0x7B)]
        [InlineData(0x0051, "2642", 123, 0x73)]
        [InlineData(0x0027, "3106", 131, 0x7B)]
        [InlineData(0x0027, "3106", 520, 0x7B)]
        public void AGloriousWrite_CarriesItsMousesLength(int pid, string firmware, int count, int length)
        {
            Assert.Equal(length, Assert.Single(WritesAtCount((ushort)pid, firmware, count))[3]);
        }

        [Fact]
        public void TheCountedRead_IsTheIoctlHidapiSends_AndFailsWithNoDevice()
        {
            // HID_OUT_CTL_CODE(100), hidapi windows/hidapi_hidclass.h:50-51.
            Assert.Equal(0xB0192u, HidReportChannel.IoctlHidGetFeature);
            // A channel that has no count answers 0, or -1 for a failed read.
            IHidReportChannel plain = new FakeReportChannel { Feature = _ => new byte[8] };
            Assert.Equal(0, plain.GetFeatureCounted(0x04, new byte[8]));
            IHidReportChannel failing = new FakeReportChannel();
            Assert.Equal(-1, failing.GetFeatureCounted(0x04, new byte[8]));
            // The real channel with no device behind its handle.
            using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In);
            var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(pipe.SafePipeHandle.DangerousGetHandle(), ownsHandle: false);
            using var real = HidReportChannel.ForHandle("pipe", handle, featureOnly: false, 8, 8, 8);
            Assert.Equal(-1, real.GetFeatureCounted(0x04, new byte[8]));
        }

        // ── A forced kill leaves the device in host control ──

        [Fact]
        public void ARoccatDirectSession_RecordsItsHandBack_BeforeItTakesControl()
        {
            KeyboardProfileBackups.ResetForTest();
            var model = Roccat("Roccat Burst Pro");
            string key = model.Map.Product;
            var io = new FakeReportChannel { Path = null, FeatureReportLength = 11 };
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Later, session.Paint(io, _ => { }, model.Map.Lights, new[] { 1, 2 }));
            Assert.Empty(io.Calls);
            Assert.NotNull(KeyboardProfileBackups.Get(key));
            Saved();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 1, 2 }));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Null(KeyboardProfileBackups.Get(key));
        }

        [Fact]
        public void WhatAKilledRunLeftOnARoccatDirectDevice_IsHandedBackWithNoClaim()
        {
            var model = Roccat("Roccat Burst Pro");
            string key = model.Map.Product;
            var io = new FakeReportChannel { Path = null, FeatureReportLength = 11 };
            var session = model.Begin(io, _ => { });
            session.Paint(io, _ => { }, model.Map.Lights, new[] { 1, 2 });
            var record = Assert.Single(session.CrashFrames);
            Assert.NotNull(model.Repair);
            io.Calls.Clear();
            Assert.True(model.Repair(io, _ => { }));
            Assert.Equal(record, Assert.Single(Sets(io)));
            Assert.Null(KeyboardProfileBackups.Get(key));
        }

        [Fact]
        public void ARoccatDirectSessionOverAKilledRunsRecord_HandsBack_EvenWithNoPictureOfItsOwn()
        {
            var model = Roccat("Roccat Burst Pro");
            var io = new FakeReportChannel { Path = null, FeatureReportLength = 11 };
            var first = model.Begin(io, _ => { });
            first.Paint(io, _ => { }, model.Map.Lights, new[] { 1, 2 });
            var record = Assert.Single(first.CrashFrames);
            var session = model.Begin(io, _ => { });
            Assert.NotEmpty(session.CrashFrames);
            io.Calls.Clear();
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(record, Assert.Single(Sets(io)));
            Assert.Equal(0, Held());
        }

        [Fact]
        public void ARoccatDeviceWithNoHandBack_RecordsNothing()
        {
            var model = Roccat("Roccat Horde Aimo");
            var io = new FakeReportChannel { Path = null, FeatureReportLength = 300 };
            var session = model.Begin(io, _ => { });
            session.Paint(io, _ => { }, model.Map.Lights, new int[model.Map.Lights.Count]);
            Assert.Equal(0, Held());
        }

        private static (FakeReportChannel Control, FakeReportChannel Led) VulcanChannels()
        {
            var control = new FakeReportChannel { Path = null };
            var led = new FakeReportChannel { Path = "led", OutputReportLength = 65, InputReportLength = 65 };
            control.Feature = id => id switch
            {
                0x0F or 0x09 => new byte[] { id, 0x00, 136, 0, 0, 0, 2, 0, 0 },
                0x04 => new byte[] { 0x04, 1, 0, 0 },
                _ => null,
            };
            return (control, led);
        }

        [Fact]
        public void AVulcanSession_RecordsItsHandBack_BeforeItEntersDirect_AndARepairSendsIt()
        {
            KeyboardProfileBackups.ResetForTest();
            var model = RoccatVulcanLighting.Instance.Model("Roccat Vulcan 120-Series Aimo", 0x1E7D, 0x3098);
            var (control, led) = VulcanChannels();
            var session = model.BeginWith(control, new IHidReportChannel[] { led }, _ => { });
            control.Calls.Clear();
            var lights = session.Map.Lights;
            var colors = new int[lights.Count];
            Assert.Equal(HidLightingPaint.Later, session.Paint(control, _ => { }, lights, colors));
            Assert.Empty(control.Calls);
            Assert.Equal(1, Held());
            Saved();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(control, _ => { }, lights, colors));

            Assert.NotNull(model.Repair);
            control.Calls.Clear();
            Assert.True(model.Repair(control, _ => { }));
            var sets = Sets(control);
            Assert.Equal(RoccatVulcanLighting.ModeReport(0x3098, 0x02), sets[0]);
            Assert.Equal(RoccatVulcanLighting.DirectSwitch(0x3098, false), sets[1]);
            Assert.Equal(0, Held());
        }

        [Fact]
        public void AVulcanSessionOverAKilledRunsRecord_HandsBack_EvenWithNoPictureOfItsOwn()
        {
            var model = RoccatVulcanLighting.Instance.Model("Roccat Vulcan 120-Series Aimo", 0x1E7D, 0x3098);
            var (control, led) = VulcanChannels();
            KeyboardProfileBackups.Take(model.Map.Product, 0, RoccatVulcanLighting.ModeReport(0x3098, 0x02));
            var session = model.BeginWith(control, new IHidReportChannel[] { led }, _ => { });
            Assert.NotEmpty(session.CrashFrames);
            control.Calls.Clear();
            Assert.True(session.HandBack(control, _ => { }));
            Assert.Equal(RoccatVulcanLighting.ModeReport(0x3098, 0x02), Sets(control)[0]);
            Assert.Equal(0, Held());
        }

        private static FakeReportChannel AuraChannel(byte[] answer = null)
            => new() { Path = null, InputReportLength = 65, OutputReportLength = 65, Input = () => answer };

        [Fact]
        public void TheClaymore_RecordsItsHandBack_BeforeItTakesRemoteControl_AndARepairSendsIt()
        {
            KeyboardProfileBackups.ResetForTest();
            var numpad = new byte[65];
            numpad[6] = 1;
            var io = AuraChannel(numpad);
            var session = AsusAuraUsbLighting.BeginKeyboard(io, 0x184D);
            Assert.DoesNotContain(io.Calls, c => c.Kind == "out" && c.Bytes[1] == 0x41);
            var lights = session.Map.Lights;
            var colors = new int[lights.Count];
            Assert.Equal(HidLightingPaint.Later, session.Paint(io, _ => { }, lights, colors));
            Assert.DoesNotContain(io.Calls, c => c.Kind == "out" && c.Bytes[1] == 0x41);
            Assert.Equal(1, Held());
            Saved();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, lights, colors));
            Assert.Contains(io.Calls, c => c.Kind == "out" && c.Bytes[1] == 0x41 && c.Bytes[2] == 0x01);
            Assert.Contains(io.Calls, c => c.Kind == "out" && c.Bytes[1] == 0x41 && c.Bytes[2] == 0x03);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(0, Held());

            var model = new AsusAuraUsbLighting().ModelFor(
                OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).Single(r => r.ProductId == 0x184D), 0x184D);
            Assert.NotNull(model.Repair);
        }

        [Fact]
        public void TheClaymoreOverAKilledRunsRecord_HandsBack_EvenWithNoPictureOfItsOwn()
        {
            var numpad = new byte[65];
            numpad[6] = 1;
            KeyboardProfileBackups.Take(AsusAuraUsbLighting.ProductKey(0x184D), 0, AsusAuraUsbLighting.RemoteControlFrame(0));
            var io = AuraChannel(numpad);
            var session = AsusAuraUsbLighting.BeginKeyboard(io, 0x184D);
            Assert.NotEmpty(session.CrashFrames);
            io.Calls.Clear();
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Contains(io.Calls, c => c.Kind == "out" && c.Bytes[1] == 0x41 && c.Bytes[2] == 0x00);
            Assert.Equal(0, Held());
        }

        [Fact]
        public void AStrixEvolveOverAKilledRunsRecord_HandsBack_EvenWithNoPictureOfItsOwn()
        {
            KeyboardProfileBackups.Take(AsusAuraUsbLighting.ProductKey(0x185B), 0, AsusAuraUsbLighting.Gen1ResetFrame());
            var model = new AsusAuraUsbLighting().ModelFor(
                OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).Single(r => r.Name == "ASUS ROG Strix Evolve"), 0x185B);
            var io = new FakeReportChannel { Path = null, FeatureReportLength = 9 };
            var session = model.Begin(io, _ => { });
            Assert.NotEmpty(session.CrashFrames);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(AsusAuraUsbLighting.Gen1ResetFrame(), Assert.Single(Sets(io)));
            Assert.Equal(0, Held());
        }

        [Fact]
        public void AStrixEvolve_RecordsItsHandBack_BeforeItEntersDirect_AndARepairSendsIt()
        {
            KeyboardProfileBackups.ResetForTest();
            var model = new AsusAuraUsbLighting().ModelFor(
                OpenRgbRegistrations.Of(AsusAuraUsbLighting.OpenRgbFamily).Single(r => r.Name == "ASUS ROG Strix Evolve"), 0x185B);
            var io = new FakeReportChannel { Path = null, FeatureReportLength = 9 };
            io.Feature = _ => new byte[9];
            var session = model.Begin(io, _ => { });
            Assert.Empty(Sets(io));
            Assert.Equal(HidLightingPaint.Later, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x102030 }));
            Assert.Empty(Sets(io));
            Assert.Equal(1, Held());
            Saved();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new[] { 0x102030 }));

            Assert.NotNull(model.Repair);
            io.Calls.Clear();
            Assert.True(model.Repair(io, _ => { }));
            Assert.Equal(AsusAuraUsbLighting.Gen1ResetFrame(), Assert.Single(Sets(io)));
            Assert.Equal(0, Held());
        }
    }
}
