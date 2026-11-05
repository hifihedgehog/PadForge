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
    /// <summary>A PC can hold two external devices of one model, each with
    /// its own stored lighting, so each keeps a backup of its own: neither
    /// picture waits on the other's save, and each hand-back writes that
    /// device's bytes.</summary>
    [Collection("PeripheralOutputStatics")]
    public class BackupPerDeviceTests : IDisposable
    {
        private const string PathA = @"\\?\hid#vid_0000&pid_0000&mi_00#7&aaaa&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string PathB = @"\\?\hid#vid_0000&pid_0000&mi_00#7&bbbb&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        public BackupPerDeviceTests() => KeyboardProfileBackups.ResetForTest(persistAtOnce: true);

        public void Dispose() => KeyboardProfileBackups.ResetForTest();

        private static int Held() => KeyboardProfileBackups.SaveRegistry(out _).Count;

        private static void Saved()
        {
            KeyboardProfileBackups.SaveRegistry(out int version);
            KeyboardProfileBackups.Persisted(version);
        }

        [Fact]
        public void AModelIsOwedForTheDeviceAtAPath_NotForEveryDeviceOfIt()
        {
            var model = KovaModel();
            KeyboardProfileBackups.Take(KeyboardProfileBackups.DeviceKey(model.Map.Product, PathA), 0, new byte[28]);
            Assert.True(model.Owed(PathA));
            Assert.False(model.Owed(PathB));
        }

        /// <summary>A device with no serial number takes a new path on another
        /// USB port. It takes over the backup it left, and a backup of a device
        /// still present stays that device's.</summary>
        [Fact]
        public void ADeviceOnAnotherPort_TakesOverTheBackupItLeft()
        {
            var gone = new HashSet<string> { PathA };
            KeyboardProfileBackups.ResetForTest(persistAtOnce: true, pathPresent: path => !gone.Contains(path));
            var model = KovaModel();
            string product = model.Map.Product;
            var left = Kova(PathA, 0x05);
            var held = (RoccatLighting.StoredSession)model.Begin(left, _ => { });
            held.Paint(left, _ => { }, model.Map.Lights, new[] { 1, 2 });

            // The mouse on its new path still shows PadForge's picture.
            var ours = RoccatLighting.KovaProfile(new byte[RoccatLighting.KovaProfileLength], 1, 2);
            var moved = new FakeReportChannel
            {
                Path = PathB,
                FeatureReportLength = RoccatLighting.KovaProfileLength,
                Feature = id => id == RoccatLighting.KovaProfileReport ? ours : new byte[8],
            };
            var session = (RoccatLighting.StoredSession)model.Begin(moved, _ => { });
            Assert.Null(KeyboardProfileBackups.Get(KeyboardProfileBackups.DeviceKey(product, PathA)));
            Assert.NotNull(KeyboardProfileBackups.Get(KeyboardProfileBackups.DeviceKey(product, PathB)));
            Assert.Equal(0x05, session.Original[RoccatLighting.KovaMode]);
        }

        [Fact]
        public void ADeviceOfAModel_LeavesTheBackupOfOneStillPresent()
        {
            KeyboardProfileBackups.ResetForTest(persistAtOnce: true, pathPresent: _ => true);
            var model = KovaModel();
            string product = model.Map.Product;
            var a = Kova(PathA, 0x05);
            var held = (RoccatLighting.StoredSession)model.Begin(a, _ => { });
            held.Paint(a, _ => { }, model.Map.Lights, new[] { 1, 2 });

            var session = (RoccatLighting.StoredSession)model.Begin(Kova(PathB, 0x06), _ => { });
            Assert.NotNull(KeyboardProfileBackups.Get(KeyboardProfileBackups.DeviceKey(product, PathA)));
            Assert.Equal(0x06, session.Original[RoccatLighting.KovaMode]);
        }

        private static FakeReportChannel Kova(string path, byte marker)
        {
            var stored = new byte[RoccatLighting.KovaProfileLength];
            stored[0] = RoccatLighting.KovaProfileReport;
            stored[1] = RoccatLighting.KovaProfileLength;
            stored[RoccatLighting.KovaMode] = marker;
            return new FakeReportChannel
            {
                Path = path,
                FeatureReportLength = RoccatLighting.KovaProfileLength,
                Feature = id => id == RoccatLighting.KovaProfileReport ? (byte[])stored.Clone() : new byte[8],
            };
        }

        private static HidLightingModel KovaModel()
            => RoccatLighting.Instance.Match(CollectionFor(OpenRgbRegistrations.Of(RoccatLighting.FamilyName).Single(r => r.Name == "Roccat Kova")));

        /// <summary>Each picture's Take of the other's bytes made a new
        /// version no save had written, so neither mouse ever lit.</summary>
        [Fact]
        public void TwoKovas_DoNotTakeTurnsOverOneBackup_AndEachGetsItsOwnBack()
        {
            KeyboardProfileBackups.ResetForTest(persistAtOnce: false);
            var model = KovaModel();
            var a = Kova(PathA, 0x05);
            var b = Kova(PathB, 0x09);
            var sessionA = model.Begin(a, _ => { });
            var sessionB = model.Begin(b, _ => { });
            var colors = new[] { 1, 2 };
            Assert.Equal(HidLightingPaint.Later, sessionA.Paint(a, _ => { }, model.Map.Lights, colors));
            Saved();
            Assert.Equal(HidLightingPaint.Later, sessionB.Paint(b, _ => { }, model.Map.Lights, colors));
            Saved();
            Assert.Equal(HidLightingPaint.Sent, sessionA.Paint(a, _ => { }, model.Map.Lights, colors));
            Assert.Equal(HidLightingPaint.Sent, sessionB.Paint(b, _ => { }, model.Map.Lights, colors));
            Assert.Equal(2, Held());

            Assert.True(sessionA.HandBack(a, _ => { }));
            Assert.Equal(1, Held());
            Assert.Equal(0x05, a.Calls.Last().Bytes[RoccatLighting.KovaMode]);
            Assert.True(sessionB.HandBack(b, _ => { }));
            Assert.Equal(0x09, b.Calls.Last().Bytes[RoccatLighting.KovaMode]);
            Assert.Equal(0, Held());
        }

        private static FakeReportChannel Redragon(string path, byte profile, byte red)
        {
            byte[] last = null;
            var io = new FakeReportChannel { Path = path, FeatureReportLength = 16 };
            io.Feature = id =>
            {
                last = io.Calls.Where(c => c.Kind == "set").Select(c => c.Bytes).LastOrDefault();
                if (id != 0x02 || last == null || last[1] != 0xF2) return null;
                var answer = new byte[16];
                answer[0] = 0x02;
                if (last[2] == 0x2C && last[3] == 0x00) answer[8] = profile;
                else if (last[2] == 0x49 && last[3] == 0x04)
                {
                    answer[8] = red;
                    answer[11] = 0x01;
                    answer[12] = 0x05;
                    answer[13] = 0x08;
                }
                return answer;
            };
            return io;
        }

        /// <summary>The second mouse took the first's backup as its own, and
        /// after the first hand-back dropped it, the second sent nothing.</summary>
        [Fact]
        public void TwoRedragonMice_EachGetTheirOwnRecordBack()
        {
            var model = RedragonMouseLighting.Model("M711", 0xFC30);
            var a = Redragon(PathA, 1, 0x11);
            var b = Redragon(PathB, 2, 0x22);
            var sessionA = model.Begin(a, _ => { });
            Assert.Equal(HidLightingPaint.Sent, sessionA.Paint(a, _ => { }, model.Map.Lights, new[] { 1 }));
            var sessionB = model.Begin(b, _ => { });
            Assert.Equal(HidLightingPaint.Sent, sessionB.Paint(b, _ => { }, model.Map.Lights, new[] { 1 }));
            Assert.Equal(2, Held());

            a.Calls.Clear();
            b.Calls.Clear();
            Assert.True(sessionA.HandBack(a, _ => { }));
            Assert.Equal(RedragonMouseLighting.RestoreFrames(1, new byte[] { 0x11, 0, 0, 1, 5, 8 }).Select(f => Convert.ToHexString(f)),
                a.Calls.Where(c => c.Kind == "set").Select(c => Convert.ToHexString(c.Bytes)));
            Assert.True(sessionB.HandBack(b, _ => { }));
            Assert.Equal(RedragonMouseLighting.RestoreFrames(2, new byte[] { 0x22, 0, 0, 1, 5, 8 }).Select(f => Convert.ToHexString(f)),
                b.Calls.Where(c => c.Kind == "set").Select(c => Convert.ToHexString(c.Bytes)));
            Assert.Equal(0, Held());
        }

        private static FakeReportChannel Zet(string path, byte marker)
        {
            var stored = new byte[59];
            stored[0] = 0x04;
            stored[21] = marker;
            return new FakeReportChannel
            {
                Path = path,
                FeatureReportLength = 59,
                Feature = id => id == 0x04 ? (byte[])stored.Clone() : null,
            };
        }

        [Fact]
        public void TwoZetMice_EachGetTheirOwnConfigurationBack()
        {
            var model = SinowealthLighting.Instance.Match(KeyboardFamilyRig.Collection(0x258A, 0x1007, 0xFF00, 1, iface: 1, feature: 59));
            var a = Zet(PathA, 0x31);
            var b = Zet(PathB, 0x32);
            var sessionA = model.Begin(a, _ => { });
            Assert.Equal(HidLightingPaint.Sent, sessionA.Paint(a, _ => { }, model.Map.Lights, new int[model.Map.Lights.Count]));
            var sessionB = model.Begin(b, _ => { });
            Assert.Equal(HidLightingPaint.Sent, sessionB.Paint(b, _ => { }, model.Map.Lights, new int[model.Map.Lights.Count]));
            Assert.True(sessionA.HandBack(a, _ => { }));
            Assert.Equal(0x31, a.Calls.Last().Bytes[21]);
            Assert.NotEmpty(sessionB.CrashFrames);
            Assert.True(sessionB.HandBack(b, _ => { }));
            Assert.Equal(0x32, b.Calls.Last().Bytes[21]);
        }

        private static ReportLogChannel K510(string path, ReportLog log, byte marker)
        {
            log.InputAnswer = o =>
            {
                if (o[3] != 0x05) return null;
                var answer = new byte[64];
                answer[0] = 0x04;
                answer[9] = marker;
                answer[10] = 0x01;
                return answer;
            };
            return new ReportLogChannel { Path = path, Device = log, OutputReportLength = 64, InputReportLength = 64 };
        }

        [Fact]
        public void TwoK510s_DoNotTakeTurnsOverOneBackup()
        {
            KeyboardProfileBackups.ResetForTest(persistAtOnce: false);
            var model = LenovoK510Lighting.Instance.Match(new HidCollectionInfo
            {
                Path = PathA, VendorId = 0x17EF, ProductId = 0x619A, UsagePage = 0xFF1C, Usage = 0x92,
                OutputReportLength = 64, InterfaceNumber = 1,
            });
            var a = K510(PathA, new ReportLog(), 0x0D);
            var b = K510(PathB, new ReportLog(), 0x0E);
            var sessionA = model.Begin(a, _ => { });
            var sessionB = model.Begin(b, _ => { });
            Assert.Equal(HidLightingPaint.Later, sessionA.Paint(a, _ => { }, model.Map.Lights, new[] { 1 }));
            Saved();
            Assert.Equal(HidLightingPaint.Later, sessionB.Paint(b, _ => { }, model.Map.Lights, new[] { 1 }));
            Saved();
            Assert.Equal(HidLightingPaint.Sent, sessionA.Paint(a, _ => { }, model.Map.Lights, new[] { 1 }));
            Assert.Equal(HidLightingPaint.Sent, sessionB.Paint(b, _ => { }, model.Map.Lights, new[] { 1 }));
        }

        private static FakeReportChannel Sagaris(string path, byte mode)
        {
            var io = new FakeReportChannel { Path = path, InputReportLength = 65, OutputReportLength = 65 };
            int seen = 0, since = 0;
            string last = null;
            io.Input = () =>
            {
                var outs = io.Calls.Where(c => c.Kind == "out").ToList();
                if (outs.Count != seen)
                {
                    seen = outs.Count;
                    last = BitConverter.ToString(outs[^1].Bytes, 0, 3);
                    since = 0;
                }
                since++;
                var answer = new byte[65];
                answer[0] = 0x06;
                if (last == "06-0B-03" && since == 1)
                {
                    answer[3] = mode;
                    answer[4] = 2;
                    return answer;
                }
                if (last == "06-0B-05" && since <= 7)
                {
                    answer[4] = (byte)since;
                    return answer;
                }
                return null;
            };
            return io;
        }

        [Fact]
        public void TwoSagarisKeyboards_DoNotTakeTurnsOverOneBackup()
        {
            KeyboardProfileBackups.ResetForTest(persistAtOnce: false);
            var model = new AsusLegacyUsbLighting().Match(CollectionFor(OpenRgbRegistrations.Of(AsusLegacyUsbLighting.OpenRgbFamily)
                .Single(r => r.Name == "ASUS Sagaris GK1100")));
            var a = Sagaris(PathA, 4);
            var b = Sagaris(PathB, 5);
            var sessionA = model.Begin(a, _ => { });
            var sessionB = model.Begin(b, _ => { });
            Assert.Equal(HidLightingPaint.Later, sessionA.Paint(a, _ => { }, model.Map.Lights, new[] { 1 }));
            Saved();
            Assert.Equal(HidLightingPaint.Later, sessionB.Paint(b, _ => { }, model.Map.Lights, new[] { 1 }));
            Saved();
            Assert.Equal(HidLightingPaint.Sent, sessionA.Paint(a, _ => { }, model.Map.Lights, new[] { 1 }));
            Assert.Equal(HidLightingPaint.Sent, sessionB.Paint(b, _ => { }, model.Map.Lights, new[] { 1 }));
        }

        private static FakeCorsairChannel Corsair(string path) => new()
        {
            Path = path,
            Answer = q =>
            {
                if (q[1] != 0x0E) return null;
                var answer = new byte[65];
                answer[1] = 0x0E;
                answer[2] = 0x01;
                answer[1 + 0x14] = 0xC0;
                return answer;
            },
        };

        /// <summary>The first hand-back dropped the one record while the
        /// second keyboard stayed in software mode.</summary>
        [Fact]
        public void TwoCorsairKeyboards_EachOweTheirOwnHandBack()
        {
            var model = new CorsairPeripheralLighting().Match(CorsairPeripheralLightingTests.Collection(0x1B13));
            var a = Corsair(PathA);
            var b = Corsair(PathB);
            var sessionA = model.Begin(a, a.Wait);
            var sessionB = model.Begin(b, b.Wait);
            var colors = new int[sessionA.Map.Lights.Count];
            Assert.Equal(HidLightingPaint.Sent, sessionA.Paint(a, a.Wait, sessionA.Map.Lights, colors));
            Assert.Equal(HidLightingPaint.Sent, sessionB.Paint(b, b.Wait, sessionB.Map.Lights, colors));
            Assert.Equal(2, Held());
            Assert.True(sessionA.HandBack(a, a.Wait));
            Assert.Equal(1, Held());
            Assert.True(sessionB.HandBack(b, b.Wait));
            Assert.Equal(0, Held());
        }
    }
}
