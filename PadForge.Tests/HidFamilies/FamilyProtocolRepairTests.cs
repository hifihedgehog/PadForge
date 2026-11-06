using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Engine;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;
using R = PadForge.Common.Input.Peripherals.RoyuanLighting;

namespace PadForge.Tests
{
    /// <summary>A channel that answers each output report from a function,
    /// queues the answer for a later read, and logs how long each read was
    /// allowed to wait.</summary>
    internal sealed class WaitLogChannel : IHidReportChannel
    {
        public string Path { get; init; } = @"\\?\hid#vid_320f&pid_5041&mi_01#fake";
        public bool FeatureOnly => false;
        public int InputReportLength { get; init; } = 65;
        public int OutputReportLength { get; init; } = 64;
        public int FeatureReportLength { get; init; } = 65;
        public readonly List<int> ReadWaits = new();
        public readonly List<byte[]> Written = new();
        public readonly Queue<byte[]> Pending = new();
        public Func<byte[], byte[]> Answer = _ => null;
        public bool FailWrites;
        public bool FailReads;

        public bool SetFeature(byte[] report) => !FailWrites;

        public bool GetFeature(byte reportId, byte[] buffer) => false;

        public bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs)
        {
            if (FailWrites) return false;
            Written.Add((byte[])report.Clone());
            var answer = Answer(report);
            if (answer != null) Pending.Enqueue(answer);
            return true;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            if (FailReads) return -1;
            ReadWaits.Add(timeoutMs);
            if (Pending.Count == 0) return 0;
            var answer = Pending.Dequeue();
            int copied = Math.Min(answer.Length, buffer.Length);
            Array.Copy(answer, buffer, copied);
            return copied;
        }

        public void Dispose() { }
    }

    /// <summary>A silent serial port, timed as Windows times a read on it: a
    /// read that gets nothing returns once the port's read bound for the
    /// bytes asked for has passed.</summary>
    internal sealed class SilentTimedPort : ISerialLineChannel
    {
        public long ElapsedMs;
        public int Reads;

        public bool Write(byte[] bytes) => true;

        public int Read(byte[] buffer)
        {
            Reads++;
            ElapsedMs += OwnLightingChannels.SerialReadBoundMs(buffer.Length);
            return 0;
        }

        public void Dispose() { }
    }

    /// <summary>A serial port that never stops sending bytes and never ends
    /// a line, until the test gives up on it.</summary>
    internal sealed class ChatteringPort : ISerialLineChannel
    {
        public volatile bool Stop;

        public bool Write(byte[] bytes) => true;

        public int Read(byte[] buffer)
        {
            if (Stop) return -1;
            buffer[0] = (byte)'x';
            return 1;
        }

        public void Dispose() { }
    }

    /// <summary>The waits on the shared worker's serial ports (#557).</summary>
    public class SerialReadBoundTests
    {
        [Fact]
        public void ASilentSerialRead_WaitsTheSameForAnyBuffer()
        {
            Assert.Equal(OwnLightingChannels.SerialReadBoundMs(1), OwnLightingChannels.SerialReadBoundMs(256));
            Assert.Equal(100, OwnLightingChannels.SerialReadBoundMs(256));
        }

        [Fact]
        public void TheSerialPort_TakesItsReadBoundFromTheseConstants()
        {
            // OpenSerial is fenced in the test host, so its timeouts are held to
            // the constants the bound is worked out from by their source line.
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string source = File.ReadAllText(Path.Combine(d.FullName, "PadForge.App", "Common", "Input", "Peripherals", "OwnLightingChannels.cs"));
            Assert.Contains("ReadTotalTimeoutConstant = SerialReadTotalMs, ReadTotalTimeoutMultiplier = SerialReadPerByteMs", source);
        }

        [Fact]
        public void ASilentDygma_HoldsTheWorkerForItsAnswerWait()
        {
            var port = new SilentTimedPort();
            Assert.Null(DygmaRaiseLighting.ReadAnswer(port, ms => port.ElapsedMs += ms));
            Assert.True(port.ElapsedMs <= DygmaRaiseLighting.AnswerWaitMs + 100, $"{port.ElapsedMs} ms");
        }

        /// <summary>A hand-back is on the stop's clock, with nothing served beside it.</summary>
        [Fact]
        public void ASilentDygma_HoldsAHandBackForLessThanTheStopWaits()
        {
            var port = new SilentTimedPort();
            var session = new DygmaRaiseLighting.Session(port, ms => port.ElapsedMs += ms, 3);
            session.Paint(null, null, Array.Empty<LightInfo>(), Array.Empty<int>());
            port.ElapsedMs = 0;
            Assert.False(session.HandBack(null, null));
            Assert.True(port.ElapsedMs <= DygmaRaiseLighting.HandBackAnswerWaitMs + 100, $"{port.ElapsedMs} ms");
        }


        [Fact]
        public async Task ADygmaAnswerThatNeverEnds_GivesUpAtItsWait()
        {
            var port = new ChatteringPort();
            var read = Task.Run(() => DygmaRaiseLighting.ReadAnswer(port, _ => { }));
            // Well past the read's own wait, or the two race on a loaded machine.
            int limitMs = DygmaRaiseLighting.AnswerWaitMs + 10_000;
            bool ended = await Task.WhenAny(read, Task.Delay(limitMs)) == read;
            port.Stop = true;
            Assert.True(ended, $"the read was still going after {limitMs} ms");
            Assert.Null(await read);
        }

        [Fact]
        public void ASilentPortAskedForItsModel_HoldsTheWorkerForAboutASecond()
        {
            var port = new SilentTimedPort();
            Assert.Null(SkydimoSerialLighting.Ask(port, out bool failed));
            Assert.False(failed);
            // One drain read and OpenRGB's ten attempts, which on Linux wait
            // 0.1 s each (serial_port.cpp:549-550).
            Assert.Equal(1 + SkydimoSerialLighting.QueryAttempts, port.Reads);
            Assert.True(port.ElapsedMs <= 1100, $"{port.ElapsedMs} ms");
        }
    }

    /// <summary>The Akko's spacing around every SET, and each board's
    /// brightness (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class RoyuanPacingTests
    {
        private static HidCollectionInfo Vendor(ushort pid, int iface)
            => KeyboardFamilyRig.Collection(0x3151, pid, 0xFF00, 0x01, iface: iface, feature: 65);

        private static byte[] Answer()
        {
            var a = new byte[65];
            a[1] = 0x87;
            a[2] = 0x05; a[3] = 0x02; a[4] = 0x03; a[5] = 0x13; a[6] = 0x0A; a[7] = 0x0B; a[8] = 0x0C;
            return a;
        }

        private static List<int> TakeWaits(KeyboardFamilyRig rig)
        {
            lock (rig.Waits)
            {
                var waits = rig.Waits.ToList();
                rig.Waits.Clear();
                return waits;
            }
        }

        [Fact]
        public void AnAkkoHandBack_WaitsOutTheSpacingAfterItsLastPicture()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 65 };
            rig.Log.FeatureAnswer = (_, _) => Answer();
            var family = new R(rig.Tick);
            using var backend = rig.Lit(new IHidLightingFamily[] { family }, Vendor(0x4003, 0), 0xFF0000, holdMs: R.AkkoPreSetMs);
            Assert.Equal(2, rig.Log.Of('F').Count);
            rig.Advance(100);
            TakeWaits(rig);
            rig.Log.Clear();
            rig.Release(backend);
            Assert.Contains(R.AkkoPostSetMs - 100, TakeWaits(rig));
            Assert.Equal("00 07 05 02 03 13 0A 0B 0C", ReportLog.Hex(Assert.Single(rig.Log.Of('F')), 9));
        }

        [Fact]
        public void ANewAkkoSession_WaitsOutTheSpacingBeforeItReads()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 65 };
            rig.Log.FeatureAnswer = (_, _) => Answer();
            var family = new R(rig.Tick);
            using var backend = rig.Lit(new IHidLightingFamily[] { family }, Vendor(0x4003, 0), 0xFF0000, holdMs: R.AkkoPreSetMs);
            rig.Advance(R.AkkoPostSetMs);
            rig.Release(backend);
            rig.Advance(50);
            TakeWaits(rig);
            rig.Log.Clear();
            KeyboardFamilyRig.Light(0x00FF00);
            KeyboardFamilyRig.Passes(backend);
            var waits = TakeWaits(rig);
            int spacing = waits.IndexOf(R.AkkoPostSetMs - 50);
            Assert.True(spacing >= 0, string.Join(",", waits));
            Assert.True(spacing < waits.IndexOf(R.ReadWaitMs), string.Join(",", waits));
            Assert.Equal("00 87", ReportLog.Hex(rig.Log.Of('F')[0], 2));
        }

        [Fact]
        public void ASkyloongHandBack_TakesNoSpacing()
        {
            using var rig = new KeyboardFamilyRig { FeatureLength = 65 };
            rig.Log.FeatureAnswer = (_, _) => Answer();
            var family = new R(rig.Tick);
            using var backend = rig.Lit(new IHidLightingFamily[] { family }, Vendor(0x5029, 2), 0x0000FF);
            TakeWaits(rig);
            rig.Release(backend);
            Assert.Empty(TakeWaits(rig));
        }

        [Fact]
        public void TheAkko_RunsAtItsMostBrightness()
        {
            // ROYUAN_AKKO_BRIGHTNESS_MAX 4 (RoyuanKeyboardController.h:99), and
            // the color carries the dimming. 07 + 01 + 04 + 04 + 08 + 60 = 0x78.
            Assert.Equal("00 07 01 04 04 08 10 20 30 87", ReportLog.Hex(R.Picture(R.Board.Akko, 0x102030), 10));
        }

        [Fact]
        public void TheOks_RunsAtItsMostBrightness()
        {
            // brightness_max 5 (RGBController_OKSKeyboard.cpp:127-129).
            Assert.Equal(5, OksKeyboardLighting.DirectBrightness);
        }
    }

    /// <summary>Which collections a family takes (#557).</summary>
    public class FamilyCollectionFilterTests
    {
        public static IEnumerable<object[]> Escapes() => new[]
        {
            new object[] { "AOCKeyboardController", AocKeyboardLighting.Instance, 'F' },
            new object[] { "AOCMouseController", AocMouseLighting.Instance, 'F' },
            new object[] { "AOCMousematController", AocMousematLighting.Instance, 'F' },
            new object[] { "ClevoLightbarController", ClevoLightbarLighting.Instance, 'F' },
            new object[] { "CreativeController", CreativeSoundBlasterLighting.Instance, 'O' },
            new object[] { "DarkProject", DarkProjectKeyboardLighting.Instance, 'O' },
            new object[] { "DreamCheekyController", DreamCheekyLighting.Instance, 'O' },
            new object[] { "EdifierHaloController", EdifierHaloLighting.Instance, 'O' },
            new object[] { "GameSirController", GameSirLighting.Instance, 'O' },
            new object[] { "GigabyteAorusMouseController", GigabyteAorusMouseLighting.Instance, 'F' },
            new object[] { "HYTEKeyboardController", HyteKeyboardLighting.Instance, 'O' },
            new object[] { "KeychronKeyboardController", KeychronKeyboardLighting.Instance, 'F' },
            new object[] { "LaviewTechnologyController", LaviewTechnologyLighting.Instance, 'F' },
            new object[] { "LightSaltController", LightSaltLighting.Instance, 'O' },
            new object[] { "LuxaforController", LuxaforLighting.Instance, 'O' },
            new object[] { "MadCatzCyborgController", MadCatzCyborgLighting.Instance, 'F' },
            new object[] { "MintakaKeyboardController", MintakaKeyboardLighting.Instance, 'F' },
            new object[] { "MSIClutchController", MsiClutchLighting.Instance, 'F' },
            new object[] { "MSIClutchController", MsiClutchLighting.Instance, 'O' },
            new object[] { "N5312AController", N5312ALighting.Instance, 'F' },
            new object[] { "NZXTMouseController", NzxtMouseLighting.Instance, 'O' },
            new object[] { "PatriotViperMouseController", PatriotViperMouseLighting.Instance, 'F' },
            new object[] { "ThermaltakePoseidonZRGBController", PoseidonZRgbLighting.Instance, 'F' },
            new object[] { "RobobloqLightStripController", RobobloqLightStripLighting.Instance, 'O' },
            new object[] { "SayoDeviceController", SayoDeviceLighting.Instance, 'O' },
            new object[] { "SkydimoHIDController", SkydimoHidLighting.Instance, 'O' },
            new object[] { "StreamDeckController", StreamDeckLighting.Instance, 'O' },
            new object[] { "TecknetController", TecknetLighting.Instance, 'F' },
            new object[] { "ThingMController", ThingMBlinkLighting.Instance, 'F' },
            new object[] { "TrustController", TrustLighting.Instance, 'F' },
            new object[] { "WinbondGamingKeyboardController", WinbondKeyboardLighting.Instance, 'O' },
            new object[] { "WushiController", WushiDockLighting.Instance, 'F' },
            new object[] { "XPGSummonerKeyboardController", XpgSummonerLighting.Instance, 'O' },
            new object[] { "ZETKeyboardController", ZetKeyboardLighting.Instance, 'O' },
            new object[] { "LenovoControllers", Lenovo4ZoneLighting.Instance, 'F' },
            new object[] { "LenovoControllers", LenovoLegionLighting.Instance, 'F' },
            new object[] { "LenovoControllers", LenovoLegionUsbLighting.Instance, 'F' },
            new object[] { "LenovoControllers", LenovoM300Lighting.Instance, 'O' },
            new object[] { "LenovoControllers", LenovoK510Lighting.Instance, 'O' },
        };

        /// <summary>A collection with none of the reports a family sends takes
        /// no lighting, so it is no unit (HidCollectionInfo: 0 is a type the
        /// collection has none of).</summary>
        [Theory]
        [MemberData(nameof(Escapes))]
        public void ACollectionWithNoReportOfTheKindAFamilySends_IsNoUnit(string folder, object instance, char kind)
        {
            var family = (IHidLightingFamily)instance;
            bool any = false;
            foreach (var r in OpenRgbRegistrations.Of(folder))
            {
                var full = CollectionFor(r, output: 2048, feature: 2048, input: 2048);
                if (family.Match(full) == null) continue;
                any = true;
                var none = kind == 'F' ? full with { FeatureReportLength = 0 } : full with { OutputReportLength = 0 };
                Assert.Null(family.Match(none));
            }
            Assert.True(any, folder + " matched no collection");
        }

        [Fact]
        public void EachMountainBoard_NeedsTheReportsItsOwnFramesTake()
        {
            // The Everest writes output reports and the Everest 60 feature
            // reports (MountainKeyboardController.cpp, Mountain60KeyboardController.cpp).
            foreach (var r in OpenRgbRegistrations.Of(MountainKeyboardLighting.OpenRgbFamily))
            {
                var full = CollectionFor(r, output: 2048, feature: 2048, input: 2048);
                Assert.NotNull(MountainKeyboardLighting.Instance.Match(full));
                var none = r.ProductId == MountainKeyboardLighting.EverestPid
                    ? full with { OutputReportLength = 0 }
                    : full with { FeatureReportLength = 0 };
                Assert.Null(MountainKeyboardLighting.Instance.Match(none));
            }
        }

        [Fact]
        public void NoFamily_LetsALengthOfZeroPastItsLengthCheck()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            var escapes = Directory.GetFiles(Path.Combine(d.FullName, "PadForge.App", "Common", "Input", "Peripherals"), "*.cs", SearchOption.AllDirectories)
                .Where(f => File.ReadAllText(f).Contains("ReportLength != 0 &&", StringComparison.Ordinal))
                .Select(Path.GetFileName);
            Assert.Empty(escapes);
        }

        [Fact]
        public void ADuckyCollectionWithoutTheOutputReportItsFramesNeed_IsNoUnit()
        {
            // OpenRGB registers interface 1 with no usage (DuckyKeyboardControllerDetect.cpp:35-36),
            // and each frame is 65 bytes (DuckyKeyboardController.cpp:59-215).
            // Aurora's device takes the collection of that length (DuckyDevice.cs:177).
            var r = OpenRgbRegistrations.Of(DuckyKeyboardLighting.OpenRgbFamily).First();
            Assert.Null(DuckyKeyboardLighting.Instance.Match(CollectionFor(r, output: 0)));
            Assert.Null(DuckyKeyboardLighting.Instance.Match(CollectionFor(r, output: 9)));
            Assert.NotNull(DuckyKeyboardLighting.Instance.Match(CollectionFor(r, output: 65)));
        }
    }

    /// <summary>A serial port opens for one program at a time, and Windows answers
    /// any other with access denied, so each family on a port can say another
    /// program has it.</summary>
    public class SerialPortHeldTests
    {
        [Fact]
        public void APortAnotherProgramHas_SaysSo_InEverySerialFamily()
        {
            try
            {
                foreach (var model in new[]
                         {
                             BlinkyTapeLighting.Instance.Model, DygmaRaiseLighting.Instance.Model,
                             HyteMousematLighting.Instance.Model, SkydimoSerialLighting.Instance.Model,
                         })
                {
                    OwnLightingChannels.SerialDenied = true;
                    Assert.True(model.OpenFoundHeld());
                    OwnLightingChannels.SerialDenied = false;
                    Assert.False(model.OpenFoundHeld());
                }
                // Each open answers for itself. The fence keeps the test host
                // from the real call, so the order is read from the source.
                string open = FenceSource.Body(AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "OwnLightingChannels.cs"),
                    "internal static ISerialLineChannel OpenSerial(string path, int baud)");
                int reset = open.IndexOf("SerialDenied = false;", StringComparison.Ordinal);
                Assert.True(reset > 0 && reset < open.IndexOf("CreateFileW(", StringComparison.Ordinal), open);
            }
            finally { OwnLightingChannels.SerialDenied = false; }
        }
    }

    /// <summary>How a keyboard's stored writes are spaced and what they leave
    /// on the keyboard (#557).</summary>
    public class StoredWriteTests
    {
        [Fact]
        public void TheGk104Pro_SpacesEveryPictureAsAStoredWrite()
        {
            // Every picture ends in LE_DEFINE_SAVE (SkyloongGK104ProController.cpp:92).
            var r = OpenRgbRegistrations.Of(SkyloongKeyboardLighting.OpenRgbFamily).Single();
            var model = SkyloongKeyboardLighting.Instance.Match(CollectionFor(r, output: 65));
            Assert.Equal(LenovoLegionLighting.HoldMs, model.HoldMs);
            Assert.Equal(LenovoLegionLighting.RepaintMs, model.RepaintMs);
        }

        [Fact]
        public void AnEffectThatTakesAColor_IsStoredLit()
        {
            var steps = MintakaKeyboardLighting.Picture(Array.Empty<LightInfo>(), Array.Empty<int>());
            var uncolored = new byte[] { 0x06, 0x08, 0x80 };
            int seen = 0;
            foreach (var step in steps.Skip(4).Take(5))
            {
                for (int j = 0; j < 4; j++)
                {
                    int page = 1 + j * MintakaKeyboardLighting.EffectPageLength;
                    string color = Hex(step.Skip(page + 1).Take(3).ToArray());
                    Assert.Equal(uncolored.Contains(step[page]) ? "00 00 00" : "FF FF FF", color);
                    seen++;
                }
            }
            Assert.Equal(20, seen);
        }
    }

    /// <summary>The Das Keyboard's opening against what OpenRGB refuses
    /// (#557).</summary>
    public class DasKeyboardOpeningTests
    {
        private static HidLightingModel Model(ushort feature)
            => DasKeyboardLighting.Instance.Match(CherryKeyboardLightingTests.CollectionOf(
                OpenRgbRegistrations.Of(DasKeyboardLighting.FamilyName).Single(r => r.Name == "Das Keyboard 4Q RGB"), feature));

        /// <summary>A version answer as ReceiveData takes it: report 1, then
        /// the length and the string, the XOR closing at zero.</summary>
        private static Func<byte, byte[]> Answering(string version)
        {
            var bytes = new List<byte> { 0x01, (byte)(version.Length + 3), 0x00, 0x00 };
            bytes.AddRange(Encoding.ASCII.GetBytes(version));
            while (bytes.Count % 8 != 7) bytes.Add(0);
            byte sum = 0;
            foreach (byte b in bytes) sum ^= b;
            bytes.Add(sum);
            var reports = new Queue<byte[]>();
            for (int at = 0; at < bytes.Count; at += 8) reports.Enqueue(bytes.Skip(at).Take(8).ToArray());
            return _ => reports.Count > 0 ? reports.Dequeue() : new byte[8];
        }

        [Fact]
        public void ABoardThatGaveNoFullVersion_GetsNoSession()
        {
            // DetectDasKeyboardControllers deletes the controller whose
            // GetLayoutString is "NONE", a version under 17 characters
            // (DasKeyboardControllerDetect.cpp:40-43, DasKeyboardController.cpp:34-44).
            var silent = new FakeReportChannel { FeatureReportLength = 9, Feature = _ => new byte[8] };
            Assert.Null(Model(9).Begin(silent, _ => { }));
            var shortAnswer = new FakeReportChannel { FeatureReportLength = 9, Feature = Answering("ABC16") };
            Assert.Null(Model(9).Begin(shortAnswer, _ => { }));
            var full = new FakeReportChannel { FeatureReportLength = 9, Feature = Answering("ABC16XYZ21.27.000") };
            Assert.NotNull(Model(9).Begin(full, _ => { }));
        }

        [Fact]
        public void ACollectionTooShortForTheModernForm_TakesTheTraditionalOne()
        {
            // A key's modern report is 11 bytes (DasKeyboardController.cpp:98-108
            // and 193-217), and Windows cuts a report to the collection's length.
            var io = new FakeReportChannel { FeatureReportLength = 8, Feature = Answering("ABC16XYZ21.27.000") };
            var session = Assert.IsType<DasKeyboardLighting.Session>(Model(8).Begin(io, _ => { }));
            Assert.True(session.Traditional);
            var wide = new FakeReportChannel { FeatureReportLength = 11, Feature = Answering("ABC16XYZ21.27.000") };
            Assert.False(Assert.IsType<DasKeyboardLighting.Session>(Model(11).Begin(wide, _ => { })).Traditional);
        }
    }

    /// <summary>What an unanswered or refused report costs a Sonix keyboard's
    /// session (#557).</summary>
    public class SonixAnswerTests
    {
        private static HidLightingModel Model(string name)
            => EVisionKeyboardLighting.Instance.Match(CherryKeyboardLightingTests.CollectionOf(
                OpenRgbRegistrations.Of("EVisionKeyboardController").Single(r => r.Name == name)));

        private const string SecondGeneration = "EVision Keyboard 320F:5041";

        private static byte[] Echo(byte[] sent, byte status = 0)
        {
            var answer = (byte[])sent.Clone();
            answer[7] = status;
            if (sent[3] == 0x03) new byte[] { 0xAA, 0x55, 0x01, 0x02, 0x03, 126, 0x04 }.CopyTo(answer, 8);
            return answer;
        }

        private static (HidLightingModel Model, HidLightingSession Session, WaitLogChannel Io) Opened()
        {
            var io = new WaitLogChannel { Answer = sent => Echo(sent) };
            var model = Model(SecondGeneration);
            var session = model.Begin(io, _ => { });
            Assert.NotNull(session);
            io.ReadWaits.Clear();
            io.Written.Clear();
            return (model, session, io);
        }

        [Fact]
        public void ARefusedQuery_LosesThatFrameAlone_AsOpenRgbGoesOnAtTheNext()
        {
            // SetLedsDirect and RefreshLedDirect drop Write's and Query's
            // results (EVisionV2KeyboardController.cpp:386 and 404), and Write
            // stops at the first error (lines 210-225).
            var (model, session, io) = Opened();
            io.Answer = sent => Echo(sent, status: 1);
            Assert.Equal(HidLightingPaint.Later, session.Paint(io, _ => { }, model.Map.Lights, new int[106]));
            Assert.True(session.LaterMs > 0);
            Assert.Single(io.Written);
            Assert.True(session.KeepAlive(io, _ => { }, model.Map.Lights, new int[106]));
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Empty(session.CrashFrames);
        }

        [Fact]
        public void ASilentKeyboard_WaitsOnce_AndItsPictureStillGoesOut()
        {
            var (model, session, io) = Opened();
            io.Answer = _ => null;
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new int[106]));
            Assert.Equal(7, io.Written.Count);
            Assert.Equal(SonixKeyboard.AnswerMs, io.ReadWaits.Sum());
            io.ReadWaits.Clear();
            Assert.True(session.KeepAlive(io, _ => { }, model.Map.Lights, new int[106]));
            Assert.Equal(0, io.ReadWaits.Sum());
            // It answers again, and the next report is waited for once more.
            io.Answer = sent => Echo(sent);
            Assert.True(session.KeepAlive(io, _ => { }, model.Map.Lights, new int[106]));
            Assert.True(session.KeepAlive(io, _ => { }, model.Map.Lights, new int[106]));
            Assert.Contains(SonixKeyboard.AnswerMs, io.ReadWaits);
        }

        [Fact]
        public void ALateAnswerToAnEarlierQuery_DoesNotCostTheOneAfterIt()
        {
            var (model, session, io) = Opened();
            byte[] earlier = null;
            io.Answer = sent =>
            {
                if (earlier != null) io.Pending.Enqueue(Echo(earlier));
                earlier = sent;
                return Echo(sent);
            };
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new int[106]));
            Assert.Equal(7, io.Written.Count);
        }

        [Fact]
        public void AFailedWriteOrRead_StillFailsTheSession()
        {
            var (model, session, io) = Opened();
            io.FailWrites = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, new int[106]));
            Assert.False(session.KeepAlive(io, _ => { }, model.Map.Lights, new int[106]));
            Assert.False(session.HandBack(io, _ => { }));
            io.FailWrites = false;
            io.FailReads = true;
            Assert.Equal(HidLightingPaint.Failed, session.Paint(io, _ => { }, model.Map.Lights, new int[106]));
        }

        [Fact]
        public void ASilentFirstGenerationKeyboard_WaitsOnceAPicture()
        {
            // Seven color reports and the mode report, each with a read that
            // would wait 1 s.
            var model = Model("EVision Keyboard 320F:5000");
            var io = new WaitLogChannel();
            var session = model.Begin(io, _ => { });
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new int[126]));
            Assert.Equal(8, io.Written.Count);
            Assert.Equal(SonixKeyboard.AnswerMs, io.ReadWaits.Sum());
            io.ReadWaits.Clear();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, _ => { }, model.Map.Lights, new int[126]));
            Assert.Equal(0, io.ReadWaits.Sum());
        }
    }

    /// <summary>A CH340 port's row before and after it answers (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class SkydimoRowNameTests
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private const string Ch340 = @"USB\VID_1A86&PID_7523\5&1&0&2";

        private static (SkydimoSerialLighting Family, FakeSerialPort Port) Family(string answer)
        {
            var port = new FakeSerialPort
            {
                Answer = bytes => Encoding.ASCII.GetString(bytes) == "Moni-A" && answer != null ? Encoding.ASCII.GetBytes(answer) : null,
            };
            var ports = new List<ComPort> { new("COM5", "USB-SERIAL CH340 (COM5)", Ch340) };
            return (new SkydimoSerialLighting(() => ports, _ => Box, _ => port, (_, _, _) => true), port);
        }

        [Fact]
        public void APortNothingHasAsked_NamesThePort_AndHearsNothing()
        {
            var (family, port) = Family("SK0402\r\n");
            var device = Assert.Single(family.Look());
            Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.Peripheral_SerialDeviceOnPort, "COM5"), device.Name);
            Assert.DoesNotContain("Skydimo", device.Name);
            Assert.Empty(port.Written());
            Assert.Equal(0, port.Reads);
        }

        [Fact]
        public void APortThatAnswers_TakesTheNameOpenRgbGivesIt()
        {
            // model_name = "Skydimo " + model_id (SkydimoSerialController.cpp:238).
            var (family, _) = Family("SK0402\r\n");
            var session = family.Model.BeginOwn(family.Model.Open(@"\\.\COM5"), _ => { });
            Assert.Equal("Skydimo SK0402", session.DeviceName);
        }

        [Fact]
        public void TheWorker_ListsThePortsName_ThenTheNameItsSessionGave()
        {
            var family = new FakeOwnFamily();
            family.Found = new[]
            {
                new OwnDevice(@"\\.\COM5", family.Model) { VendorId = 0x1A86, ProductId = 0x7523, Container = Box, Name = "Serial device on COM5" },
            };
            family.SessionName = "Skydimo SK0402";
            using var rig = new KeyboardFamilyRig();
            using var backend = rig.Started(new IHidLightingFamily[] { family }, Array.Empty<HidCollectionInfo>());
            Assert.Equal("Serial device on COM5", backend.Snapshot.Units.Single().Name);
            KeyboardFamilyRig.Link(new OutputPath(OutputFamily.HidLightingUnit, @"\\.\COM5"));
            KeyboardFamilyRig.Light(0x102030);
            KeyboardFamilyRig.Passes(backend, 3);
            Assert.Equal("Skydimo SK0402", backend.Snapshot.Units.Single().Name);
            // The name stays with the port through a reopen.
            rig.Release(backend);
            Assert.Equal("Skydimo SK0402", backend.Snapshot.Units.Single().Name);
        }
    }

    /// <summary>The AW2724DM's own name and product (#557).</summary>
    public class Aw2724dmIdentityTests
    {
        [Fact]
        public void TheAw2724dm_KeepsItsOwnNameAndProduct()
        {
            // 0x1010: { name: "AW2724DM" } (Alienware_Monitor_Gen3_Controller.js:332-333).
            var collection = new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_187c&pid_1010#1", VendorId = AlienwareMonitorLighting.VendorId,
                ProductId = AlienwareMonitorLighting.Aw2724Dm, OutputReportLength = AlienwareMonitorLighting.DwfReportSize,
                UsagePage = 0xFF00, Usage = 1,
            };
            var model = AlienwareMonitorLighting.Instance.Match(collection);
            Assert.Equal("Alienware AW2724DM", model.Name);
            Assert.Equal("alienware:187C:1010", model.Map.Product);
            var dwf = AlienwareMonitorLighting.Instance.Match(collection with { ProductId = AlienwareMonitorLighting.Aw3423Dwf });
            Assert.Equal("Alienware AW3423DWF", dwf.Name);
            Assert.Equal("alienware:187C:100E", dwf.Map.Product);
        }
    }

    /// <summary>The G600's hand-back follows the active profile (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class G600ProfileFollowTests
    {
        private static byte[] Active(int profile) => new byte[] { 0xF0, (byte)(profile << 4), 0, 0 };

        private static byte[] Profile(int index, byte red)
        {
            var report = new byte[LogitechG600Lighting.ProfileReportLength];
            report[0] = (byte)(LogitechG600Lighting.FirstProfileReportId + index);
            report[1] = red;
            return report;
        }

        private static (FakeReportChannel Io, Func<int> Reads, Action<int> Switch, Action<bool> Fail) Mouse()
        {
            int active = 0, reads = 0;
            bool fail = false;
            var io = new FakeReportChannel { FeatureReportLength = LogitechG600Lighting.ProfileReportLength };
            io.Feature = id =>
            {
                reads++;
                if (fail) return null;
                return id == LogitechG600Lighting.ActiveReportId ? Active(active) : Profile(id - LogitechG600Lighting.FirstProfileReportId, (byte)(0x10 + id - LogitechG600Lighting.FirstProfileReportId));
            };
            return (io, () => reads, p => active = p, f => fail = f);
        }

        [Fact]
        public void TheModel_ReadsTheActiveProfileAgain()
        {
            var collection = KeyboardFamilyRig.Collection(LogitechG600Lighting.VendorId, LogitechG600Lighting.ProductId,
                LogitechG600Lighting.UsagePage, 1, iface: LogitechG600Lighting.Interface, feature: 154);
            Assert.True(LogitechG600Lighting.Instance.Match(collection).RecheckMs > 0);
        }

        [Fact]
        public void AProfileSwitch_MovesTheHandBack_AndSendsThePictureAgain()
        {
            var (io, _, select, _) = Mouse();
            var session = LogitechG600Lighting.Session.Begin(io);
            Assert.Equal(0x10, Assert.Single(session.CrashFrames)[1]);
            Assert.True(session.Recheck(io, _ => { }));
            Assert.False(session.RepaintDue);
            select(1);
            Assert.True(session.Recheck(io, _ => { }));
            Assert.Equal(0x11, Assert.Single(session.CrashFrames)[1]);
            Assert.True(session.RepaintDue);
            Assert.True(session.HandBack(io, _ => { }));
            Assert.Equal(0x11, io.Calls.Last(c => c.Kind == "set").Bytes[1]);
        }

        [Fact]
        public void ARecheckWithNoSwitch_ReadsOnlyTheActiveProfile()
        {
            var (io, reads, _, _) = Mouse();
            var session = LogitechG600Lighting.Session.Begin(io);
            int before = reads();
            Assert.True(session.Recheck(io, _ => { }));
            Assert.Equal(before + 1, reads());
        }

        [Fact]
        public void AFailedRecheck_KeepsTheHandBackAndTheSession()
        {
            var (io, _, select, fail) = Mouse();
            var session = LogitechG600Lighting.Session.Begin(io);
            var frames = session.CrashFrames;
            fail(true);
            select(2);
            Assert.True(session.Recheck(io, _ => { }));
            Assert.Same(frames, session.CrashFrames);
            Assert.False(session.RepaintDue);
        }
    }
}
